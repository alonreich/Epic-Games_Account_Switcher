using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        private bool _isUnsavedLoginScanRunning;
        private Forms.NotifyIcon _notifyIcon;
        private bool _isExplicitExit;
        public MainWindow()
        {
            InitializeComponent();
            ViewModel = new MainViewModel();
            DataContext = ViewModel;
            ViewModel.RequestCapture += ViewModel_RequestCapture;
            ViewModel.OnAccountsChanged += UpdateTrayContextMenu;
            Loaded += MainWindow_Loaded;
            if (!ApplySavedWindowPlacement(AppSettings.Instance)) WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await ViewModel.LoadAccountsAsync();
                await ViewModel.PromptForUnsavedLoginIfNeeded();
                UpdateTrayIconVisibility();
                StartUnsavedLoginMonitor();
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
        }
        private async Task<bool> ViewModel_RequestCapture(MainViewModel.AccountMetadata metadata)
        {
            var captureWin = new CaptureAccountWindow(metadata.Name, "", metadata.Email, metadata.ImagePath, "", metadata.Id, true, false) { Owner = this };
            if (captureWin.ShowDialog() == true) { return await ExecuteCapture(captureWin); }
            return false;
        }
        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (WindowState == WindowState.Minimized && AppSettings.Instance.MinimizeToTray)
            {
                Hide();
            }
        }
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (AppSettings.Instance.MinimizeToTray && !_isExplicitExit)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            if (_notifyIcon != null) { _notifyIcon.Visible = false; _notifyIcon.Dispose(); _notifyIcon = null; }
            try { ViewModel.Shutdown(); } catch { }
            try { if (_unsavedLoginTimer != null) _unsavedLoginTimer.Stop(); } catch { }
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
            var header = new Forms.ToolStripMenuItem("--- Fast Switcher ---") { Enabled = false };
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
                        ViewModel.SwitchCommand.Execute(acc);
                    }
                    UpdateTrayContextMenu();
                };
                menu.Items.Add(item);
            }

            if (ViewModel.Accounts.Count == 0)
            {
                menu.Items.Add(new Forms.ToolStripMenuItem("No accounts saved") { Enabled = false });
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
                new CustomMessageBox("Log File Unavailable", "The log file could not be opened:\n\n" + ex.Message, MessageBoxVisualStyle.Error).ShowDialog();
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
            if (Keyboard.Modifiers == ModifierKeys.Control)
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
        private async Task<bool> ExecuteCapture(CaptureAccountWindow captureWin)
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
                            var r = new CustomMessageBox("Overwrite Account?", "Account '" + existingName + "' already exists for this Epic ID.\n\nDo you want to overwrite it with the current login data?", "Overwrite", "Cancel").ShowDialog();
                            return r == true;
                        })));
                    }),
                    false,
                    cancellationToken);
                if (result.Item1) { await ViewModel.LoadAccountsAsync(); ViewModel.ShowToast("Account captured successfully!"); return true; }
                else if (result.Item2 != null) { new CustomMessageBox("Capture Failed", result.Item2, MessageBoxVisualStyle.Error).ShowDialog(); }
                return false;
            }
            catch (OperationCanceledException)
            {
                ViewModel.ShowToast("Operation cancelled.");
                return false;
            }
            finally { ViewModel.EndExternalBusyOperation(); }
        }
    }
    public class AnimatedWrapPanel : WrapPanel
    {
        private readonly Dictionary<UIElement, Rect> _lastLayoutSlots = new Dictionary<UIElement, Rect>();
        protected override Size ArrangeOverride(Size finalSize)
        {
            var arrangedSize = base.ArrangeOverride(finalSize);
            var currentSlots = new Dictionary<UIElement, Rect>();
            foreach (UIElement child in InternalChildren)
            {
                var frameworkChild = child as FrameworkElement;
                if (frameworkChild == null) continue;
                var currentSlot = LayoutInformation.GetLayoutSlot(frameworkChild);
                currentSlots[child] = currentSlot;
                Rect previousSlot;
                if (_lastLayoutSlots.TryGetValue(child, out previousSlot))
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
                    var gradient = new LinearGradientBrush(
                        ((Color)ColorConverter.ConvertFromString("#1E293B")),
                        ((Color)ColorConverter.ConvertFromString("#0F172A")),
                        new Point(0, 0), new Point(1, 1));
                    dc.DrawRoundedRectangle(gradient, new Pen(((Brush)new BrushConverter().ConvertFromString("#3B82F6")), 2), new Rect(0, 0, size, size), 64, 64);

                    var iconBrush = ((Brush)new BrushConverter().ConvertFromString("#94A3B8"));
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
