using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Threading.Tasks;
using System.Windows.Threading;
using Epic_Games_Account_Switcher_Globals;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace Epic_Games_Account_Switcher_Client
{
    public partial class MainWindow : Window
    {
        public MainViewModel ViewModel { get; set; }
        private Point _dragStartPoint;
        private DateTime _mouseDownTimestamp;
        private Account _draggedAccount;
        private ListBoxItem _draggedContainer;
        private List<Account> _orderBeforeDrag = new List<Account>();
        private bool _didPreviewReorder;
        private bool _isDragInProgress;
        private DateTime _lastPreviewMoveAt = DateTime.MinValue;
        private int _lastPreviewIndex = -1;
        private ListBoxItem _dropPreviewContainer;
        private DispatcherTimer _unsavedLoginTimer;
        private DispatcherTimer _clockTimer;
        private bool _isUnsavedLoginScanRunning;
        private Forms.NotifyIcon _notifyIcon;
        private bool _isExplicitExit;
        private bool _isForcedExit;
        private bool _exitWhenIdle;
        private DateTime _settingsClosedAt = DateTime.MinValue;
        private Button _settingsOpenedFrom;
        private IInputElement _focusBeforeBusy;
        private LauncherFrameController _launcherFrame;
        public MainWindow()
        {
            InitializeComponent();
            ViewModel = new MainViewModel();
            DataContext = ViewModel;
            ViewModel.RequestCapture += ViewModel_RequestCapture;
            ViewModel.OnAccountsChanged += UpdateTrayContextMenu;
            ViewModel.OnAccountsChanged += () => ViewModel.RefreshFrameAccounts();
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            ViewModel.SwitchCompleted += ViewModel_SwitchCompleted;
            SettingsPopup.CustomPopupPlacementCallback = (popupSize, targetSize, offset) =>
                new[] { new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width, targetSize.Height), PopupPrimaryAxis.Horizontal) };
            Activated += (s, e) => _launcherFrame?.OnActivated();
            Loaded += MainWindow_Loaded;
            if (!ApplySavedWindowPlacement(AppSettings.Instance)) WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        private void ViewModel_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.IsBusy):
                    OnBusyChanged(ViewModel.IsBusy);
                    break;
                case nameof(MainViewModel.ToastMessage):
                case nameof(MainViewModel.IsToastVisible):
                    if (ViewModel.IsToastVisible) AnnounceLiveRegion(ToastText);
                    break;
                case nameof(MainViewModel.BusyStatus):
                    if (ViewModel.IsBusy) AnnounceLiveRegion(BusyStatusText);
                    break;
                case nameof(MainViewModel.ShowUnsavedLoginBanner):
                    if (ViewModel.ShowUnsavedLoginBanner) AnnounceLiveRegion(UnsavedBanner);
                    break;
                case nameof(MainViewModel.IsUpdateBannerVisible):
                    if (ViewModel.IsUpdateBannerVisible) AnnounceLiveRegion(UpdateBanner);
                    break;
                case nameof(MainViewModel.Accounts):
                    UpdateTrayToolTip();
                    break;
            }
        }

        /// <summary>Tells screen readers that a polite live region changed (WPF does not raise this by itself).</summary>
        private void AnnounceLiveRegion(UIElement element)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var peer = UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element);
                    peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
                }
                catch { }
            }), DispatcherPriority.Background);
        }

        /// <summary>Moves keyboard focus into the busy overlay while an operation runs and back afterwards; exits if the user asked to close during it.</summary>
        private void OnBusyChanged(bool busy)
        {
            if (busy)
            {
                _focusBeforeBusy = Keyboard.FocusedElement;
                Dispatcher.BeginInvoke(new Action(() => { if (ViewModel.IsBusy) BusyCancelButton.Focus(); }), DispatcherPriority.Input);
                return;
            }

            if (_exitWhenIdle)
            {
                _exitWhenIdle = false;
                Dispatcher.BeginInvoke(new Action(() => { _isExplicitExit = true; Close(); }), DispatcherPriority.Background);
                return;
            }

            var previous = _focusBeforeBusy as UIElement;
            _focusBeforeBusy = null;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (previous != null && previous.IsVisible && previous.IsEnabled && previous.Focusable) previous.Focus();
            }), DispatcherPriority.Input);
        }

        private void ViewModel_SwitchCompleted(Account account, bool success)
        {
            UpdateTrayToolTip();
            if (IsVisible || _notifyIcon == null || account == null) return;
            if (success) ShowTrayNotice("Switched account", "Epic Games Launcher is now signed in as " + account.Name + ".", Forms.ToolTipIcon.Info);
            else ShowTrayNotice("Switch failed", "Could not switch to " + account.Name + ". Open the app for details.", Forms.ToolTipIcon.Warning);
        }

        private void ShowTrayNotice(string title, string text, Forms.ToolTipIcon icon)
        {
            try { _notifyIcon?.ShowBalloonTip(5000, title, text, icon); } catch { }
        }

        private void UpdateTrayToolTip()
        {
            if (_notifyIcon == null || ViewModel?.Accounts == null) return;
            var active = ViewModel.Accounts.FirstOrDefault(a => a != null && a.IsActive);
            var text = active == null ? "Epic Games Account Switcher" : "Epic Account Switcher - " + active.Name;
            if (text.Length > 63) text = text.Substring(0, 60) + "...";
            try { _notifyIcon.Text = text; } catch { }
        }

        private void CardMenu_Click(object sender, RoutedEventArgs e)
        {
            var item = FindAncestor<ListBoxItem>(sender as DependencyObject);
            var card = item?.Template?.FindName("CardButton", item) as Button;
            if (card?.ContextMenu == null) return;
            card.ContextMenu.PlacementTarget = card;
            card.ContextMenu.Placement = PlacementMode.Bottom;
            card.ContextMenu.IsOpen = true;
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            // A click on the button while the popup is open first closes it (light dismiss); don't reopen it straight away.
            if (DateTime.UtcNow - _settingsClosedAt < TimeSpan.FromMilliseconds(250)) return;
            // The same popup serves the normal header button and the frame-mode button.
            _settingsOpenedFrom = sender as Button ?? SettingsButton;
            SettingsPopup.PlacementTarget = _settingsOpenedFrom;
            SettingsPopup.Placement = ReferenceEquals(_settingsOpenedFrom, SettingsButton) ? PlacementMode.Custom : PlacementMode.Top;
            SettingsPopup.IsOpen = true;
        }

        private void SettingsPopup_Opened(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() => FirstSettingCheckBox.Focus()), DispatcherPriority.Input);
        }

        private void SettingsPopup_Closed(object sender, EventArgs e)
        {
            _settingsClosedAt = DateTime.UtcNow;
        }

        private void SettingsPopup_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            SettingsPopup.IsOpen = false;
            (_settingsOpenedFrom ?? SettingsButton).Focus();
            e.Handled = true;
        }

        private void ExitApp_Click(object sender, RoutedEventArgs e)
        {
            SettingsPopup.IsOpen = false;
            _isExplicitExit = true;
            Close();
        }

        private void Toast_HoldChanged(object sender, MouseEventArgs e)
        {
            ViewModel.IsToastHeld = ToastBorder.IsMouseOver || ToastBorder.IsKeyboardFocusWithin;
        }

        private void Toast_IsKeyboardFocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            ViewModel.IsToastHeld = ToastBorder.IsMouseOver || ToastBorder.IsKeyboardFocusWithin;
        }
        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await ViewModel.LoadAccountsAsync();
                await ViewModel.PromptForUnsavedLoginIfNeeded();
                UpdateTrayIconVisibility();
                StartUnsavedLoginMonitor();
                _ = ViewModel.CheckForUpdatesOnStartupAsync();
                ApplyLauncherFrameSetting(AppSettings.Instance.ExperimentalLauncherFrame);
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("Main window startup load failed", ex);
            }
        }

        private void StartUnsavedLoginMonitor()
        {
            if (_unsavedLoginTimer != null) return;
            _unsavedLoginTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            _unsavedLoginTimer.Tick += async (s, args) =>
            {
                if (_isDragInProgress || _isUnsavedLoginScanRunning || ViewModel == null || ViewModel.IsBusy || AccountService.SessionOperationLock.CurrentCount == 0) return;
                _isUnsavedLoginScanRunning = true;
                try { await ViewModel.PromptForUnsavedLoginIfNeeded(); }
                catch (Exception ex) { Globals.WriteToLog("Unsaved login monitor scan failed", ex); }
                finally { _isUnsavedLoginScanRunning = false; }
            };
            _unsavedLoginTimer.Start();

            _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            _clockTimer.Tick += (s, args) => { if (IsVisible && !_isDragInProgress) ViewModel?.RefreshRelativeTimes(); };
            _clockTimer.Start();
        }
        private enum CaptureOutcome { Saved, Cancelled, Failed }

        private async Task<bool> ViewModel_RequestCapture(MainViewModel.AccountMetadata metadata)
        {
            string name = metadata.Name, description = "", email = metadata.Email, image = metadata.ImagePath, password = "";
            while (true)
            {
                var captureWin = new CaptureAccountWindow(name, description, email, image, password, metadata.Id, true, false) { Owner = this };
                if (captureWin.ShowDialog() != true) return false;
                var outcome = await ExecuteCapture(captureWin);
                if (outcome != CaptureOutcome.Failed) return outcome == CaptureOutcome.Saved;
                // Keep what the user typed so a retry doesn't start from scratch.
                name = captureWin.AccountName;
                description = captureWin.AccountDescription;
                email = captureWin.AccountEmail;
                image = captureWin.ProfileImagePath;
                password = captureWin.AccountPassword;
            }
        }
        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (WindowState == WindowState.Minimized && AppSettings.Instance.MinimizeToTray)
            {
                HideToTray();
            }
        }

        /// <summary>Hides the window to the notification area; the first time, explains that the app is still running.</summary>
        private void HideToTray()
        {
            Hide();
            var settings = AppSettings.Instance;
            if (settings.TrayHintShown || _notifyIcon == null) return;
            ShowTrayNotice("Still running", "Epic Games Account Switcher keeps running here. Click the icon to open it, or right-click and choose Exit to quit.", Forms.ToolTipIcon.Info);
            settings.TrayHintShown = true;
            AppSettings.SaveSettings();
        }
        /// <summary>Turns the experimental launcher frame on or off.</summary>
        public void ApplyLauncherFrameSetting(bool enabled)
        {
            if (_launcherFrame == null) _launcherFrame = new LauncherFrameController(this);
            _launcherFrame.SetEnabled(enabled);
        }

        /// <summary>Called by LauncherFrameController: swaps between the normal card grid and the compact frame layout.</summary>
        public void SetFrameLayout(bool frame)
        {
            ViewModel?.RefreshFrameAccounts();
            FrameContent.Visibility = frame ? Visibility.Visible : Visibility.Collapsed;
            NormalContent.Visibility = frame ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>False when Windows "Show animations" is off (reduced motion): frame transitions then switch instantly.</summary>
        public static bool AnimationsEnabled => SystemParameters.ClientAreaAnimation;
        public static readonly TimeSpan FadeInDuration = TimeSpan.FromMilliseconds(220);

        /// <summary>Fades the whole window (it is layered, so Opacity applies to the top-level window).</summary>
        public void FadeWindow(double to, TimeSpan duration, Action completed)
        {
            var animation = new DoubleAnimation(to, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            animation.Completed += (s, e) =>
            {
                BeginAnimation(OpacityProperty, null);
                Opacity = to;
                completed?.Invoke();
            };
            BeginAnimation(OpacityProperty, animation);
        }

        /// <summary>Frame-mode entrance: the window fades in while the side cards slide in from the edges and the glow appears.</summary>
        public void PlayFrameEnter()
        {
            if (!AnimationsEnabled) { Opacity = 1; return; }
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            FadeWindow(1, FadeInDuration, null);
            SlideIn(FrameLeftPanel, -40, TimeSpan.Zero, easing);
            SlideIn(FrameRightPanel, 40, TimeSpan.FromMilliseconds(60), easing);
            FrameGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 0.55, TimeSpan.FromMilliseconds(320)) { BeginTime = TimeSpan.FromMilliseconds(80), EasingFunction = easing });
        }

        private static void SlideIn(FrameworkElement panel, double fromX, TimeSpan delay, IEasingFunction easing)
        {
            var transform = panel.RenderTransform as TranslateTransform;
            if (transform == null) { transform = new TranslateTransform(); panel.RenderTransform = transform; }
            var duration = TimeSpan.FromMilliseconds(260);
            // Start values as base values, so a delayed panel doesn't flash at its final place before it starts moving.
            transform.X = fromX;
            panel.Opacity = 0;
            var slide = new DoubleAnimation(0, duration) { BeginTime = delay, EasingFunction = easing };
            slide.Completed += (s, e) => { transform.BeginAnimation(TranslateTransform.XProperty, null); transform.X = 0; };
            var fade = new DoubleAnimation(1, duration) { BeginTime = delay, EasingFunction = easing };
            fade.Completed += (s, e) => { panel.BeginAnimation(OpacityProperty, null); panel.Opacity = 1; };
            transform.BeginAnimation(TranslateTransform.XProperty, slide);
            panel.BeginAnimation(OpacityProperty, fade);
        }

        /// <summary>Closes the app for real (bypasses minimize-to-tray). Used by the tray Exit item, setup's graceful-close request and the updater.</summary>
        public void ExitApplication()
        {
            _isExplicitExit = true;
            _isForcedExit = true;
            Close();
            try { Application.Current.Shutdown(); } catch { }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (AppSettings.Instance.MinimizeToTray && !_isExplicitExit)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            if (!_isForcedExit && ViewModel != null && ViewModel.IsBusy)
            {
                e.Cancel = true;
                _isExplicitExit = false;
                if (_exitWhenIdle) return;
                if (!IsVisible) RestoreFromTray();
                var choice = new CustomMessageBox("Operation In Progress",
                    "\"" + (ViewModel.BusyStatus ?? "An operation") + "\" is still running.\n\nClosing now could leave Epic Games Launcher half-switched. Wait for it to finish, or cancel it and close once it has stopped safely.",
                    "Cancel It and Close", "Keep Waiting", null, MessageBoxVisualStyle.Danger);
                choice.ShowDialog();
                if (choice.Result == CustomDialogResult.Primary && ViewModel.IsBusy)
                {
                    _exitWhenIdle = true;
                    if (ViewModel.CancelBusyCommand.CanExecute(null)) ViewModel.CancelBusyCommand.Execute(null);
                }
                return;
            }
            if (_notifyIcon != null) { _notifyIcon.Visible = false; _notifyIcon.Dispose(); _notifyIcon = null; }
            try { _launcherFrame?.Dispose(); _launcherFrame = null; } catch { }
            try { ViewModel.Shutdown(); } catch { }
            try { if (_unsavedLoginTimer != null) _unsavedLoginTimer.Stop(); } catch { }
            try { if (_clockTimer != null) _clockTimer.Stop(); } catch { }
            SaveWindowPlacement();
            AppSettings.SaveSettings();
            base.OnClosing(e);
        }
        public void UpdateTrayIconVisibility()
        {
            if (ViewModel == null) return;
            var enableTray = AppSettings.Instance.MinimizeToTray;
            if (enableTray)
            {
                if (_notifyIcon == null)
                {
                    _notifyIcon = new Forms.NotifyIcon
                    {
                        Text = "Epic Games Account Switcher",
                        Visible = true
                    };
                    try
                    {
                        Drawing.Icon appIcon = null;
                        try
                        {
                            var uri = new Uri("pack://application:,,,/Application_Icons/Epic-Games_Account_Switcher.ico", UriKind.RelativeOrAbsolute);
                            var resInfo = Application.GetResourceStream(uri);
                            if (resInfo != null && resInfo.Stream != null) appIcon = new Drawing.Icon(resInfo.Stream);
                        }
                        catch { }

                        if (appIcon == null)
                        {
                            var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Application_Icons", "Epic-Games_Account_Switcher.ico");
                            if (File.Exists(iconPath)) appIcon = new Drawing.Icon(iconPath);
                        }

                        if (appIcon == null)
                        {
                            var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
                            if (File.Exists(exePath)) appIcon = Drawing.Icon.ExtractAssociatedIcon(exePath);
                        }

                        _notifyIcon.Icon = appIcon ?? Drawing.SystemIcons.Application;
                    }
                    catch { _notifyIcon.Icon = Drawing.SystemIcons.Application; }

                    _notifyIcon.DoubleClick += (s, e) => RestoreFromTray();
                    _notifyIcon.MouseClick += (s, e) => { if (e.Button == Forms.MouseButtons.Left) RestoreFromTray(); };
                }
                _notifyIcon.Visible = true;
                UpdateTrayContextMenu();
                UpdateTrayToolTip();
            }
            else
            {
                if (_notifyIcon != null)
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                    _notifyIcon = null;
                }
            }
        }
        public void UpdateTrayContextMenu()
        {
            if (_notifyIcon == null || ViewModel == null) return;
            var menu = new Forms.ContextMenuStrip();
            var header = new Forms.ToolStripMenuItem("Switch account") { Enabled = false };
            menu.Items.Add(header);
            menu.Items.Add(new Forms.ToolStripSeparator());

            foreach (var acc in ViewModel.Accounts)
            {
                var itemText = acc.Name;
                if (acc.IsActive) itemText += " (Active)";
                var item = new Forms.ToolStripMenuItem(itemText) { Enabled = !acc.IsActive };
                item.Click += (s, e) =>
                {
                    if (ViewModel.IsBusy)
                    {
                        ViewModel.ShowToast("Cannot switch while an operation is running.");
                        return;
                    }
                    if (ViewModel.SwitchCommand.CanExecute(acc))
                    {
                        if (!IsVisible) ShowTrayNotice("Switching account", "Switching Epic Games Launcher to " + acc.Name + "…", Forms.ToolTipIcon.Info);
                        ViewModel.SwitchCommand.Execute(acc);
                    }
                    UpdateTrayContextMenu();
                };
                menu.Items.Add(item);
            }

            if (ViewModel.Accounts.Count == 0)
            {
                menu.Items.Add(new Forms.ToolStripMenuItem("No accounts saved yet") { Enabled = false });
            }

            menu.Items.Add(new Forms.ToolStripSeparator());
            var dashboardItem = new Forms.ToolStripMenuItem("Open Dashboard");
            dashboardItem.Click += (s, e) => RestoreFromTray();
            menu.Items.Add(dashboardItem);

            var exitItem = new Forms.ToolStripMenuItem("Exit");
            exitItem.Click += (s, e) =>
            {
                _isExplicitExit = true;
                Close();
            };
            menu.Items.Add(exitItem);

            var oldMenu = _notifyIcon.ContextMenuStrip;
            _notifyIcon.ContextMenuStrip = menu;
            if (oldMenu != null) oldMenu.Dispose();
        }
        /// <summary>Called when the app is launched again while running: brings this window back, also from the notification area.</summary>
        public void ShowFromOtherInstance()
        {
            RestoreFromTray();
            // Briefly topmost so Windows lets the window come to the front over the app the user launched from.
            Topmost = true;
            Topmost = false;
        }

        private void RestoreFromTray()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Focus();
        }

        private bool ApplySavedWindowPlacement(AppSettings settings)
        {
            if (settings == null || settings.WindowWidth <= 0 || settings.WindowHeight <= 0) return false;
            try
            {
                var width = Math.Max(settings.WindowWidth, 700);
                var height = Math.Max(settings.WindowHeight, 450);
                var left = settings.WindowLeft;
                var top = settings.WindowTop;
                if (left == -1 || top == -1) return false;

                var testRect = new Drawing.Rectangle((int)left, (int)top, 100, 100);
                var targetScreen = Forms.Screen.AllScreens.FirstOrDefault(s => s.WorkingArea.IntersectsWith(testRect));
                if (targetScreen == null)
                {
                    settings.WindowLeft = -1;
                    settings.WindowTop = -1;
                    AppSettings.SaveSettings();
                    return false;
                }

                var work = targetScreen.WorkingArea;
                if (work.Width > 0) width = Math.Min(width, work.Width);
                if (work.Height > 0) height = Math.Min(height, work.Height);

                Width = width;
                Height = height;
                Left = Clamp(left, work.Left, Math.Max(work.Left, work.Right - (int)width));
                Top = Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - (int)height));
                WindowStartupLocation = WindowStartupLocation.Manual;
                return true;
            }
            catch { return false; }
        }

        private void SaveWindowPlacement()
        {
            var settings = AppSettings.Instance;
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            if (bounds.Width <= 0 || bounds.Height <= 0 || double.IsNaN(bounds.Left) || double.IsNaN(bounds.Top)) return;

            var rect = new Drawing.Rectangle((int)Math.Round(bounds.Left), (int)Math.Round(bounds.Top), Math.Max(1, (int)Math.Round(bounds.Width)), Math.Max(1, (int)Math.Round(bounds.Height)));
            var screen = Forms.Screen.FromRectangle(rect);
            settings.WindowWidth = rect.Width;
            settings.WindowHeight = rect.Height;
            settings.WindowLeft = rect.Left;
            settings.WindowTop = rect.Top;
            settings.WindowMonitorDeviceName = screen == null ? "" : screen.DeviceName;
            settings.WindowRelativeLeft = screen == null ? -1 : rect.Left - screen.WorkingArea.Left;
            settings.WindowRelativeTop = screen == null ? -1 : rect.Top - screen.WorkingArea.Top;
        }

        private static double Clamp(double value, double min, double max)
        {
            if (max < min) return min;
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void Close_Click(object sender, RoutedEventArgs e) => Close();
        private void ShowLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var logPath = Globals.GetLogPath();
                var logDir = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrWhiteSpace(logDir) && !Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
                if (!File.Exists(logPath)) File.WriteAllText(logPath, "");

                try
                {
                    Process.Start(new ProcessStartInfo { FileName = logPath, UseShellExecute = true });
                }
                catch
                {
                    Process.Start(new ProcessStartInfo { FileName = "notepad.exe", Arguments = "\"" + logPath + "\"", UseShellExecute = false });
                }
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("Failed to open log file from dashboard", ex);
                string logPath = null;
                try { logPath = Globals.GetLogPath(); } catch { }
                new CustomMessageBox("Log File Unavailable", "The log file could not be opened automatically." + (string.IsNullOrWhiteSpace(logPath) ? "" : "\n\nYou can open it yourself in Notepad:\n" + logPath), MessageBoxVisualStyle.Error).ShowDialog();
            }
        }
        private void AccountsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_isDragInProgress) return;
            _dragStartPoint = e.GetPosition(AccountsList);
            _mouseDownTimestamp = DateTime.UtcNow;
            _draggedContainer = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            _draggedAccount = _draggedContainer == null ? null : _draggedContainer.DataContext as Account;
        }
        private void AccountsList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_isDragInProgress || e.LeftButton != MouseButtonState.Pressed || _draggedAccount == null || _draggedContainer == null) return;
            var current = e.GetPosition(AccountsList);
            var delta = current - _dragStartPoint;
            if (DateTime.UtcNow - _mouseDownTimestamp < TimeSpan.FromMilliseconds(180)) return;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            StartAccountDrag();
        }
        private void StartAccountDrag()
        {
            if (_isDragInProgress || _draggedAccount == null || _draggedContainer == null || AccountsList == null) return;
            var dragSourceContainer = _draggedContainer;
            var draggedAccount = _draggedAccount;
            _orderBeforeDrag = ViewModel.Accounts.ToList();
            _didPreviewReorder = false;
            _lastPreviewIndex = ViewModel.Accounts.IndexOf(draggedAccount);
            _lastPreviewMoveAt = DateTime.MinValue;
            _isDragInProgress = true;
            try
            {
                dragSourceContainer.Opacity = 0.45;
                var data = new DataObject(typeof(Account), draggedAccount);
                var effect = DragDrop.DoDragDrop(dragSourceContainer, data, DragDropEffects.Move);
                if (effect != DragDropEffects.Move && _didPreviewReorder) RestoreOrderBeforeDrag();
            }
            finally
            {
                if (dragSourceContainer != null) dragSourceContainer.Opacity = 1.0;
                ClearDropPreview();
                _draggedAccount = null;
                _draggedContainer = null;
                _lastPreviewIndex = -1;
                _isDragInProgress = false;
            }
        }
        private void AccountsList_DragOver(object sender, DragEventArgs e)
        {
            if (!_isDragInProgress || !e.Data.GetDataPresent(typeof(Account))) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
            var dragged = e.Data.GetData(typeof(Account)) as Account;
            if (dragged == null) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
            var now = DateTime.UtcNow;
            if (now - _lastPreviewMoveAt < TimeSpan.FromMilliseconds(55)) { e.Effects = DragDropEffects.Move; e.Handled = true; return; }
            var pointer = e.GetPosition(AccountsList);
            var targetIndex = GetClosestAccountIndex(pointer);
            MoveAccountPreview(dragged, targetIndex);
            SetDropPreview(targetIndex);
            _lastPreviewMoveAt = now;
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
        private void AccountsList_DragLeave(object sender, DragEventArgs e)
        {
            ClearDropPreview();
        }
        private void AccountsList_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(Account))) { e.Handled = true; return; }
            if (_didPreviewReorder)
            {
                ViewModel.CommitAccountOrderChange(_orderBeforeDrag);
            }
            ClearDropPreview();
            e.Handled = true;
        }
        private void MoveAccountPreview(Account dragged, int toIndex)
        {
            var fromIndex = ViewModel.Accounts.IndexOf(dragged);
            if (fromIndex < 0 || toIndex < 0 || fromIndex == toIndex || toIndex == _lastPreviewIndex) return;
            ViewModel.Accounts.Move(fromIndex, toIndex);
            _didPreviewReorder = true;
            _lastPreviewIndex = toIndex;
        }
        private void RestoreOrderBeforeDrag()
        {
            ViewModel.Accounts.Clear();
            foreach (var acc in _orderBeforeDrag) ViewModel.Accounts.Add(acc);
        }
        private void SetDropPreview(int targetIndex)
        {
            if (targetIndex < 0 || ViewModel == null || ViewModel.Accounts == null || targetIndex >= ViewModel.Accounts.Count)
            {
                ClearDropPreview();
                return;
            }

            var account = ViewModel.Accounts[targetIndex];
            var container = AccountsList.ItemContainerGenerator.ContainerFromItem(account) as ListBoxItem;
            if (container == null || ReferenceEquals(container, _dropPreviewContainer)) return;

            ClearDropPreview();
            container.Tag = "DropTarget";
            _dropPreviewContainer = container;
        }
        private void ClearDropPreview()
        {
            if (_dropPreviewContainer != null && string.Equals(_dropPreviewContainer.Tag as string, "DropTarget", StringComparison.Ordinal))
            {
                _dropPreviewContainer.ClearValue(FrameworkElement.TagProperty);
            }
            _dropPreviewContainer = null;
        }
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control && ViewModel != null && ViewModel.UndoReorderCommand.CanExecute(null))
            {
                ViewModel.UndoReorderCommand.Execute(null);
                e.Handled = true;
            }
        }
        private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Zoom only applies to the normal layout; the compact frame layout has a fixed size.
            if (Keyboard.Modifiers == ModifierKeys.Control && NormalContent.Visibility == Visibility.Visible)
            {
                if (e.Delta > 0)
                {
                    ViewModel?.ZoomInCommand?.Execute(null);
                }
                else if (e.Delta < 0)
                {
                    ViewModel?.ZoomOutCommand?.Execute(null);
                }
                e.Handled = true;
            }
        }
        private int GetClosestAccountIndex(Point position)
        {
            if (ViewModel == null || ViewModel.Accounts == null || ViewModel.Accounts.Count == 0) return -1;
            var closestIndex = -1;
            var closestDistance = double.MaxValue;
            for (var i = 0; i < ViewModel.Accounts.Count; i++)
            {
                var account = ViewModel.Accounts[i];
                var container = AccountsList.ItemContainerGenerator.ContainerFromItem(account) as ListBoxItem;
                if (container == null) continue;
                var topLeft = container.TranslatePoint(new Point(0, 0), AccountsList);
                var center = new Point(topLeft.X + container.ActualWidth / 2, topLeft.Y + container.ActualHeight / 2);
                var dx = center.X - position.X;
                var dy = center.Y - position.Y;
                var distance = dx * dx + dy * dy;
                if (distance < closestDistance) { closestDistance = distance; closestIndex = i; }
            }
            return closestIndex;
        }
        private static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null) { if (current is T typed) return typed; current = VisualTreeHelper.GetParent(current); }
            return null;
        }
        private async Task<CaptureOutcome> ExecuteCapture(CaptureAccountWindow captureWin)
        {
            var cancellationToken = ViewModel.BeginExternalBusyOperation("Capturing current login...");
            try
            {
                var result = await AccountService.CaptureCurrentAccount(
                    captureWin.AccountName, 
                    captureWin.AccountEmail, 
                    captureWin.ProfileImagePath, 
                    captureWin.AccountDescription,
                    captureWin.AccountPassword,
                    new Action<string>((status) => { Dispatcher.Invoke(new Action(() => ViewModel.BusyStatus = status)); }), 
                    new Func<string, Task<bool>>((existingName) => 
                    {
                        return Task.FromResult((bool)Dispatcher.Invoke(new Func<bool>(() => 
                        {
                            var r = new CustomMessageBox("Replace Saved Account?", "'" + existingName + "' is already saved for this Epic account.\n\nReplace its saved sign-in and details with the current login? The old saved sign-in can't be brought back.", "Replace", "Cancel", null, MessageBoxVisualStyle.Danger).ShowDialog();
                            return r == true;
                        })));
                    }),
                    false,
                    cancellationToken);
                if (result.Item1) { await ViewModel.LoadAccountsAsync(); ViewModel.ShowToast("Account '" + captureWin.AccountName + "' saved."); return CaptureOutcome.Saved; }
                if (result.Item2 == null) return CaptureOutcome.Cancelled;
                ViewModel.EndExternalBusyOperation();
                var retry = new CustomMessageBox("Account Not Saved", result.Item2 + "\n\nYour details are kept, so you can try again.", "Try Again", "Close", null, MessageBoxVisualStyle.Error);
                retry.ShowDialog();
                return retry.Result == CustomDialogResult.Primary ? CaptureOutcome.Failed : CaptureOutcome.Cancelled;
            }
            catch (OperationCanceledException)
            {
                ViewModel.ShowToast("Cancelled. The account was not saved.");
                return CaptureOutcome.Cancelled;
            }
            finally { if (ViewModel.IsBusy) ViewModel.EndExternalBusyOperation(); }
        }
    }
    public class AnimatedWrapPanel : WrapPanel
    {
        private readonly Dictionary<UIElement, Rect> _lastLayoutSlots = new Dictionary<UIElement, Rect>();
        protected override Size ArrangeOverride(Size finalSize)
        {
            var arrangedSize = base.ArrangeOverride(finalSize);
            // Respect Windows "Show animations" (reduced motion): cards jump to their new place without sliding.
            var animate = SystemParameters.ClientAreaAnimation;
            var currentSlots = new Dictionary<UIElement, Rect>();
            foreach (UIElement child in InternalChildren)
            {
                var frameworkChild = child as FrameworkElement;
                if (frameworkChild == null) continue;
                var currentSlot = LayoutInformation.GetLayoutSlot(frameworkChild);
                currentSlots[child] = currentSlot;
                Rect previousSlot;
                if (animate && _lastLayoutSlots.TryGetValue(child, out previousSlot))
                {
                    var offsetX = previousSlot.Left - currentSlot.Left;
                    var offsetY = previousSlot.Top - currentSlot.Top;
                    if (Math.Abs(offsetX) > 0.1 || Math.Abs(offsetY) > 0.1)
                    {
                        if (frameworkChild.Opacity < 0.99) continue;
                        var transform = child.RenderTransform as TranslateTransform;
                        if (transform == null) { transform = new TranslateTransform(); child.RenderTransform = transform; }
                        transform.BeginAnimation(TranslateTransform.XProperty, null);
                        transform.BeginAnimation(TranslateTransform.YProperty, null);
                        transform.X = offsetX;
                        transform.Y = offsetY;
                        var duration = TimeSpan.FromMilliseconds(220);
                        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
                        transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, duration) { EasingFunction = easing });
                        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, duration) { EasingFunction = easing });
                    }
                }
            }
            _lastLayoutSlots.Clear();
            foreach (var pair in currentSlots) _lastLayoutSlots[pair.Key] = pair.Value;
            return arrangedSize;
        }
    }
    public class BooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => (value is bool && (bool)value) ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotImplementedException();
    }
    public class InverseBooleanToEnabledConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => (value is bool) ? !(bool)value : true;
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotImplementedException();
    }

    public class ProfileImageConverter : IValueConverter
    {
        private static BitmapSource _defaultAvatar;
        private static BitmapSource GetDefaultAvatar()
        {
            if (_defaultAvatar != null) return _defaultAvatar;
            try
            {
                int size = 128;
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    var background = ThemeBrush("CardBackground", "#292E42");
                    var outline = ThemeBrush("IconFill", "#7AA2F7");
                    var iconBrush = ThemeBrush("SubTextForeground", "#A9B1D6");
                    dc.DrawRoundedRectangle(background, new Pen(outline, 2), new Rect(0, 0, size, size), 64, 64);

                    dc.DrawEllipse(iconBrush, null, new Point(64, 46), 20, 20);
                    
                    var geom = new StreamGeometry();
                    using (var ctx = geom.Open())
                    {
                        ctx.BeginFigure(new Point(24, 104), true, true);
                        ctx.ArcTo(new Point(104, 104), new Size(40, 45), 0, false, SweepDirection.Clockwise, true, false);
                    }
                    dc.DrawGeometry(iconBrush, null, geom);
                }
                var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(dv);
                bmp.Freeze();
                _defaultAvatar = bmp;
                return _defaultAvatar;
            }
            catch { return null; }
        }

        private static Brush ThemeBrush(string key, string fallback)
        {
            return (Application.Current?.TryFindResource(key) as Brush) ?? (Brush)new BrushConverter().ConvertFromString(fallback);
        }

        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            var path = value as string;
            if (string.IsNullOrWhiteSpace(path)) return GetDefaultAvatar();

            try
            {
                var fullPath = Path.GetFullPath(path);
                if (!File.Exists(fullPath)) return GetDefaultAvatar();

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.None;
                bitmap.UriSource = new Uri(fullPath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return GetDefaultAvatar();
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotImplementedException();
    }
}
