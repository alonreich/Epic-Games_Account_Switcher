using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Epic_Games_Account_Switcher_Globals;

namespace Epic_Games_Account_Switcher_Client
{
    /// <summary>
    /// In-app updates: quiet daily check at startup + manual "Check for updates".
    /// The banner offers Update now / Later / Skip this version. Nothing is downloaded until the user clicks Update now,
    /// and nothing is installed or restarted until the user confirms the verified download.
    /// </summary>
    public partial class MainViewModel
    {
        private static readonly TimeSpan AutoCheckInterval = TimeSpan.FromHours(24);
        private UpdateInfo _availableUpdate;
        private bool _isCheckingForUpdates;
        private CancellationTokenSource _updateDownloadCts;

        public string AppVersionLabel => "v" + Globals.Version;

        private bool _isUpdateBannerVisible;
        public bool IsUpdateBannerVisible
        {
            get => _isUpdateBannerVisible;
            set { _isUpdateBannerVisible = value; OnPropertyChanged(); }
        }

        private string _updateBannerTitle;
        public string UpdateBannerTitle
        {
            get => _updateBannerTitle;
            set { _updateBannerTitle = value; OnPropertyChanged(); }
        }

        private string _updateBannerNotes;
        public string UpdateBannerNotes
        {
            get => _updateBannerNotes;
            set { _updateBannerNotes = value; OnPropertyChanged(); }
        }

        private bool _isUpdateDownloading;
        public bool IsUpdateDownloading
        {
            get => _isUpdateDownloading;
            set { _isUpdateDownloading = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsUpdateIdle)); CommandManager.InvalidateRequerySuggested(); }
        }
        public bool IsUpdateIdle => !IsUpdateDownloading;

        private double _updateDownloadProgress;
        public double UpdateDownloadProgress
        {
            get => _updateDownloadProgress;
            set { _updateDownloadProgress = value; OnPropertyChanged(); }
        }

        public bool AutoCheckForUpdatesSetting
        {
            get => AppSettings.Instance.AutoCheckForUpdates;
            set { AppSettings.Instance.AutoCheckForUpdates = value; AppSettings.SaveSettings(); OnPropertyChanged(); }
        }

        private ICommand _checkForUpdatesCommand, _installUpdateCommand, _remindLaterCommand, _skipUpdateCommand, _viewReleaseNotesCommand, _cancelUpdateDownloadCommand;
        public ICommand CheckForUpdatesCommand => _checkForUpdatesCommand ?? (_checkForUpdatesCommand = new AsyncRelayCommand(() => RunUpdateCheckAsync(true), () => !_isCheckingForUpdates && !IsUpdateDownloading));
        public ICommand InstallUpdateCommand => _installUpdateCommand ?? (_installUpdateCommand = new AsyncRelayCommand(InstallUpdateAsync, () => _availableUpdate != null && !IsBusy && !IsUpdateDownloading));
        public ICommand RemindLaterCommand => _remindLaterCommand ?? (_remindLaterCommand = new RelayCommand(RemindLater, () => !IsUpdateDownloading));
        public ICommand SkipUpdateCommand => _skipUpdateCommand ?? (_skipUpdateCommand = new RelayCommand(SkipThisVersion, () => _availableUpdate != null && !IsUpdateDownloading));
        public ICommand ViewReleaseNotesCommand => _viewReleaseNotesCommand ?? (_viewReleaseNotesCommand = new RelayCommand(OpenReleaseNotes, () => _availableUpdate != null));
        public ICommand CancelUpdateDownloadCommand => _cancelUpdateDownloadCommand ?? (_cancelUpdateDownloadCommand = new RelayCommand(() => _updateDownloadCts?.Cancel(), () => IsUpdateDownloading));

        /// <summary>Fire-and-forget from window load. Never blocks the UI and stays silent on any failure.</summary>
        public async Task CheckForUpdatesOnStartupAsync()
        {
            try
            {
                var s = AppSettings.Instance;
                if (!s.AutoCheckForUpdates) return;
                if (DateTime.UtcNow - s.LastUpdateCheckUtc < AutoCheckInterval) return;
                await Task.Delay(TimeSpan.FromSeconds(8));
                await RunUpdateCheckAsync(false);
            }
            catch (Exception ex) { Globals.WriteToLog("[UPDATE] Startup check failed", ex); }
        }

        private async Task RunUpdateCheckAsync(bool userInitiated)
        {
            if (_isCheckingForUpdates) return;
            _isCheckingForUpdates = true;
            CommandManager.InvalidateRequerySuggested();
            try
            {
                if (userInitiated) ShowToast("Checking for updates...");
                var result = await UpdateService.CheckAsync();
                if (result.Status == UpdateCheckStatus.UpToDate || result.Status == UpdateCheckStatus.UpdateAvailable)
                {
                    AppSettings.Instance.LastUpdateCheckUtc = DateTime.UtcNow;
                    AppSettings.SaveSettings();
                }
                Globals.WriteToLog("[UPDATE] Check result: " + result.Status + " - " + result.Message);

                if (result.Status == UpdateCheckStatus.UpdateAvailable)
                {
                    var s = AppSettings.Instance;
                    bool suppressed = string.Equals(s.SkippedUpdateVersion, result.Info.Version, StringComparison.OrdinalIgnoreCase) || DateTime.UtcNow < s.UpdateRemindAfterUtc;
                    if (!userInitiated && suppressed) return;
                    ShowUpdateBanner(result.Info);
                }
                else if (userInitiated)
                {
                    ShowToast(result.Message);
                }
            }
            finally
            {
                _isCheckingForUpdates = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private void ShowUpdateBanner(UpdateInfo info)
        {
            _availableUpdate = info;
            UpdateBannerTitle = "Version " + info.Version + " is available  (you have " + Globals.Version + ")";
            UpdateBannerNotes = SummarizeReleaseNotes(info.ReleaseNotes);
            UpdateDownloadProgress = 0;
            IsUpdateBannerVisible = true;
            CommandManager.InvalidateRequerySuggested();
        }

        private static string SummarizeReleaseNotes(string markdown)
        {
            if (string.IsNullOrWhiteSpace(markdown)) return "Improvements and fixes.";
            var lines = markdown.Replace("\r", "").Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith("#") && !l.StartsWith("---") && !l.StartsWith("<!--"))
                .Select(l => l.TrimStart('-', '*', ' ').Replace("**", ""))
                .Where(l => l.Length > 0)
                .Take(3)
                .Select(l => "• " + (l.Length > 110 ? l.Substring(0, 107) + "..." : l));
            var text = string.Join("\n", lines);
            return string.IsNullOrWhiteSpace(text) ? "Improvements and fixes." : text;
        }

        private void RemindLater()
        {
            AppSettings.Instance.UpdateRemindAfterUtc = DateTime.UtcNow.Add(AutoCheckInterval);
            AppSettings.SaveSettings();
            IsUpdateBannerVisible = false;
            ShowToast("OK, we'll remind you tomorrow.");
        }

        private void SkipThisVersion()
        {
            if (_availableUpdate == null) return;
            AppSettings.Instance.SkippedUpdateVersion = _availableUpdate.Version;
            AppSettings.SaveSettings();
            IsUpdateBannerVisible = false;
            ShowToast("Version " + _availableUpdate.Version + " skipped. You can still update from Options.");
        }

        private void OpenReleaseNotes()
        {
            var url = _availableUpdate?.ReleasePageUrl;
            if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) return;
            try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
            catch (Exception ex) { Globals.WriteToLog("[UPDATE] Could not open release notes", ex); }
        }

        private async Task InstallUpdateAsync()
        {
            var info = _availableUpdate;
            if (info == null || IsBusy) return;
            _updateDownloadCts = new CancellationTokenSource();
            IsUpdateDownloading = true;
            string installer;
            try
            {
                var progress = new Progress<double>(p => UpdateDownloadProgress = Math.Round(p * 100));
                installer = await UpdateService.DownloadAndVerifyAsync(info, progress, _updateDownloadCts.Token);
            }
            catch (OperationCanceledException)
            {
                ShowToast("Update download cancelled.");
                return;
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("[UPDATE] Download/verify failed", ex);
                new CustomMessageBox("Update Not Installed", ex.Message + "\n\nYour current version keeps working. You can try again later or download the update from the release page.", MessageBoxVisualStyle.Error).ShowDialog();
                return;
            }
            finally
            {
                IsUpdateDownloading = false;
                _updateDownloadCts?.Dispose();
                _updateDownloadCts = null;
            }

            var confirm = new CustomMessageBox("Update Ready",
                "Version " + info.Version + " is downloaded and verified.\n\nThe app will close, install the update and reopen by itself. Your saved accounts are kept.",
                "Install & Restart Now", "Later", null, MessageBoxVisualStyle.Success);
            confirm.ShowDialog();
            if (confirm.Result != CustomDialogResult.Primary)
            {
                ShowToast("The update is saved. Click UPDATE NOW whenever you're ready.");
                return;
            }
            if (IsBusy)
            {
                ShowToast("Please wait for the current operation to finish, then click UPDATE NOW again.");
                return;
            }
            try
            {
                UpdateService.LaunchInstaller(installer);
                if (Application.Current.MainWindow is MainWindow mw) mw.ExitApplication();
                else Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("[UPDATE] Could not start installer", ex);
                new CustomMessageBox("Update Not Installed", "The installer could not be started: " + ex.Message, MessageBoxVisualStyle.Error).ShowDialog();
            }
        }
    }
}
