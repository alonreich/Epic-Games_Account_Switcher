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
        private bool _isCapturing = false;
        private CancellationTokenSource _cts = new CancellationTokenSource();

        public EmbeddedLoginWindow()
        {
            InitializeComponent();
            Closed += (s, e) => _cts.Cancel();
            InitializeWebView();
        }

        private async void InitializeWebView()
        {
            try
            {
                string userDataFolder = Path.Combine(Path.GetTempPath(), "EpicSwitcherWebView2_" + Guid.NewGuid().ToString("N"));
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await LoginWebView.EnsureCoreWebView2Async(env);
                
                LoginWebView.CoreWebView2.CookieManager.DeleteAllCookies();
                LoginWebView.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
                LoginWebView.CoreWebView2.WebResourceResponseReceived += CoreWebView2_WebResourceResponseReceived;

                LoginWebView.CoreWebView2.Navigate("https://www.epicgames.com/id/login?redirectUrl=https%3A%2F%2Fwww.epicgames.com%2Fid%2Fapi%2Fexchange");
            }
            catch (Exception ex)
            {
                new CustomMessageBox("Initialization Error", "Failed to initialize Embedded Browser. Make sure WebView2 Runtime is installed.\n\n" + ex.Message, MessageBoxVisualStyle.Error).ShowDialog();
                Close();
            }
        }

        private void CoreWebView2_NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (_isCapturing) e.Cancel = true;
        }

        private async void CoreWebView2_WebResourceResponseReceived(object sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            if (_isCapturing) return;
            var uri = e.Request.Uri;
            if (uri.Contains("/id/api/exchange") && e.Response.StatusCode == 200)
            {
                _isCapturing = true;
                Dispatcher.Invoke(() =>
                {
                    LoadingOverlay.Visibility = Visibility.Visible;
                    LoginWebView.Visibility = Visibility.Hidden;
                });

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
                    Dispatcher.Invoke(() =>
                    {
                        new CustomMessageBox("Capture Error", "Failed to extract exchange code: " + ex.Message, MessageBoxVisualStyle.Error).ShowDialog();
                        _isCapturing = false;
                        LoadingOverlay.Visibility = Visibility.Collapsed;
                        LoginWebView.Visibility = Visibility.Visible;
                        LoginWebView.CoreWebView2.Navigate("https://www.epicgames.com/id/login?redirectUrl=https%3A%2F%2Fwww.epicgames.com%2Fid%2Fapi%2Fexchange");
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
                        new CustomMessageBox("Capture Failed", result.Item2, MessageBoxVisualStyle.Error).ShowDialog();
                        _isCapturing = false;
                        LoadingOverlay.Visibility = Visibility.Collapsed;
                        LoginWebView.Visibility = Visibility.Visible;
                        LoginWebView.CoreWebView2.Navigate("https://www.epicgames.com/id/login?redirectUrl=https%3A%2F%2Fwww.epicgames.com%2Fid%2Fapi%2Fexchange");
                    }
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    new CustomMessageBox("Critical Error", "An unexpected error occurred during account capture.\n\n" + ex.Message, MessageBoxVisualStyle.Error).ShowDialog();
                    Close();
                });
            }
        }
    }
}
