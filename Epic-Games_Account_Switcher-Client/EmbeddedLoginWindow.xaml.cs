using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Epic_Games_Account_Switcher_Globals;
using Newtonsoft.Json.Linq;

namespace Epic_Games_Account_Switcher_Client
{
    public partial class EmbeddedLoginWindow : Window
    {
        private const string LoginUrl = "https://www.epicgames.com/id/login?redirectUrl=https%3A%2F%2Fwww.epicgames.com%2Fid%2Fapi%2Fexchange";
        private bool _isCapturing = false;
        private bool _pageShown = false;
        private CancellationTokenSource _cts = new CancellationTokenSource();
        private const string ProfileFolderPrefix = "EpicSwitcherWebView2_";
        private readonly string _userDataFolder = Path.Combine(Path.GetTempPath(), ProfileFolderPrefix + Guid.NewGuid().ToString("N"));

        public EmbeddedLoginWindow()
        {
            InitializeComponent();
            Closed += (s, e) =>
            {
                _cts.Cancel();
                // The browser profile holds Epic sign-in cookies: close the browser and erase it.
                try { LoginWebView.Dispose(); } catch { }
                DeleteProfileFolderAsync(_userDataFolder);
            };
            InitializeWebView();
        }

        /// <summary>Erases a sign-in browser profile. The browser processes exit a moment after the window closes, so this retries briefly.</summary>
        private static void DeleteProfileFolderAsync(string folder)
        {
            Task.Run(async () =>
            {
                for (var attempt = 0; attempt < 20 && Directory.Exists(folder); attempt++)
                {
                    try { Directory.Delete(folder, true); }
                    catch { await Task.Delay(500); }
                }
                if (Directory.Exists(folder)) Globals.WriteToLog("Embedded sign-in: browser profile could not be erased yet; it will be removed at next start.");
            });
        }

        /// <summary>Startup sweep for sign-in browser profiles left behind by a crash or a locked file.</summary>
        public static void CleanupLeftoverProfiles()
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(Path.GetTempPath(), ProfileFolderPrefix + "*"))
                {
                    try
                    {
                        if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) < TimeSpan.FromMinutes(10)) continue;
                        Directory.Delete(dir, true);
                    }
                    catch (Exception ex) { Globals.WriteToLog("Embedded sign-in: could not erase an old browser profile", ex); }
                }
            }
            catch (Exception ex) { Globals.WriteToLog("Embedded sign-in: browser profile sweep failed", ex); }
        }

        private async void InitializeWebView()
        {
            try
            {
                var env = await CoreWebView2Environment.CreateAsync(null, _userDataFolder);
                await LoginWebView.EnsureCoreWebView2Async(env);

                LoginWebView.CoreWebView2.CookieManager.DeleteAllCookies();
                LoginWebView.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
                LoginWebView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
                LoginWebView.CoreWebView2.WebResourceResponseReceived += CoreWebView2_WebResourceResponseReceived;

                LoginWebView.CoreWebView2.Navigate(LoginUrl);
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("Embedded sign-in: WebView2 initialization failed", ex);
                new CustomMessageBox("Sign-In Window Unavailable", "The sign-in window needs the Microsoft Edge WebView2 Runtime, which could not be started.\n\nInstall or repair \"Microsoft Edge WebView2 Runtime\" from Microsoft, then try again. You can still use ADD SIGNED-IN ACCOUNT with Epic Games Launcher.", MessageBoxVisualStyle.Error).ShowDialog();
                Close();
            }
        }

        private void ShowOverlay(string text, bool showRetry)
        {
            LoadingText.Text = text;
            LoadingProgress.Visibility = showRetry ? Visibility.Collapsed : Visibility.Visible;
            RetryButton.Visibility = showRetry ? Visibility.Visible : Visibility.Collapsed;
            LoadingOverlay.Visibility = Visibility.Visible;
            LoginWebView.Visibility = Visibility.Hidden;
        }

        private void ShowPage()
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            LoginWebView.Visibility = Visibility.Visible;
        }

        private void CoreWebView2_NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (_isCapturing) e.Cancel = true;
        }

        private void CoreWebView2_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (_isCapturing) return;
            if (e.IsSuccess) { _pageShown = true; ShowPage(); return; }
            if (!_pageShown)
            {
                Globals.WriteToLog("Embedded sign-in: page failed to load (" + e.WebErrorStatus + ")");
                ShowOverlay("The Epic Games sign-in page couldn't be loaded. Check your internet connection, then try again.", true);
            }
        }

        private void Retry_Click(object sender, RoutedEventArgs e)
        {
            if (LoginWebView.CoreWebView2 == null) return;
            ShowOverlay("Opening the Epic Games sign-in page…", false);
            LoginWebView.CoreWebView2.Navigate(LoginUrl);
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _cts.Cancel();
            DialogResult = false;
            Close();
        }

        private void RestartSignIn()
        {
            _isCapturing = false;
            _pageShown = false;
            ShowOverlay("Opening the Epic Games sign-in page…", false);
            LoginWebView.CoreWebView2.Navigate(LoginUrl);
        }

        private async void CoreWebView2_WebResourceResponseReceived(object sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            if (_isCapturing) return;
            var uri = e.Request.Uri;
            if (uri.Contains("/id/api/exchange") && e.Response.StatusCode == 200)
            {
                _isCapturing = true;
                Dispatcher.Invoke(() => ShowOverlay("Signed in. Saving your account… Please wait.", false));

                try
                {
                    var stream = await e.Response.GetContentAsync();
                    using (var reader = new StreamReader(stream))
                    {
                        var json = await reader.ReadToEndAsync();

                        var doc = JObject.Parse(json);
                        if (doc["code"] != null)
                        {
                            string exchangeCode = doc["code"].ToString();
                            await ProcessExchangeCodeAndCaptureAsync(exchangeCode);
                        }
                        else
                        {
                            throw new Exception("Exchange code not found in response.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Globals.WriteToLog("Embedded sign-in: could not read the sign-in result", ex);
                    Dispatcher.Invoke(() =>
                    {
                        new CustomMessageBox("Account Not Saved", "You signed in, but the app couldn't read the result from Epic Games. Please sign in again.", MessageBoxVisualStyle.Error).ShowDialog();
                        RestartSignIn();
                    });
                }
            }
        }

        private async Task ProcessExchangeCodeAndCaptureAsync(string exchangeCode)
        {
            try
            {
                var result = await AccountService.CaptureFromExchangeCodeAsync(exchangeCode, msg =>
                {
                    Dispatcher.Invoke(() => LoadingText.Text = msg);
                }, _cts.Token);

                Dispatcher.Invoke(() =>
                {
                    if (result.Item1)
                    {
                        DialogResult = true;
                        Close();
                    }
                    else
                    {
                        new CustomMessageBox("Account Not Saved", result.Item2, MessageBoxVisualStyle.Error).ShowDialog();
                        RestartSignIn();
                    }
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Globals.WriteToLog("Embedded sign-in: unexpected capture failure", ex);
                Dispatcher.Invoke(() =>
                {
                    new CustomMessageBox("Account Not Saved", "Something unexpected stopped the account from being saved. Please try again.\n\nIf it keeps happening, use SHOW LOGS for details.", MessageBoxVisualStyle.Error).ShowDialog();
                    Close();
                });
            }
        }
    }
}
