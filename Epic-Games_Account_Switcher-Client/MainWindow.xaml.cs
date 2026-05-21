using System;
using System.Collections.Generic;
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
        public MainWindow()
        {
            InitializeComponent();
            ViewModel = new MainViewModel();
            DataContext = ViewModel;
            ViewModel.RequestCapture += ViewModel_RequestCapture;
            Loaded += MainWindow_Loaded;
            if (!ApplySavedWindowPlacement(AppSettings.Instance)) WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        private void MainWindow_Loaded(object sender, RoutedEventArgs e) { ViewModel.LoadAccounts(); }
        private async Task<bool> ViewModel_RequestCapture(MainViewModel.AccountMetadata metadata)
        {
            var captureWin = new CaptureAccountWindow(metadata.Name, "", metadata.Email, metadata.ImagePath, "", metadata.Id, true, true) { Owner = this };
            if (captureWin.ShowDialog() == true) { return await ExecuteCapture(captureWin); }
            return false;
        }
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            try { ViewModel.Shutdown(); } catch { }
            SaveWindowPlacement();
            AppSettings.SaveSettings();
            base.OnClosing(e);
        }

        private bool ApplySavedWindowPlacement(AppSettings settings)
        {
            if (settings == null || settings.WindowWidth <= 0 || settings.WindowHeight <= 0) return false;

            var savedScreen = Forms.Screen.AllScreens.FirstOrDefault(s => string.Equals(s.DeviceName, settings.WindowMonitorDeviceName, StringComparison.OrdinalIgnoreCase));
            var screen = savedScreen ?? Forms.Screen.FromRectangle(new Drawing.Rectangle(settings.WindowLeft, settings.WindowTop, settings.WindowWidth, settings.WindowHeight));
            if (screen == null) return false;

            var work = screen.WorkingArea;
            var width = Math.Max(settings.WindowWidth, 700);
            var height = Math.Max(settings.WindowHeight, 450);
            if (work.Width > 0) width = Math.Min(width, work.Width);
            if (work.Height > 0) height = Math.Min(height, work.Height);
            var left = settings.WindowLeft;
            var top = settings.WindowTop;

            if (savedScreen != null && settings.WindowRelativeLeft >= 0 && settings.WindowRelativeTop >= 0)
            {
                left = work.Left + settings.WindowRelativeLeft;
                top = work.Top + settings.WindowRelativeTop;
            }

            if (left == -1 || top == -1) return false;

            Width = width;
            Height = height;
            Left = Clamp(left, work.Left, work.Right - width);
            Top = Clamp(top, work.Top, work.Bottom - height);
            WindowStartupLocation = WindowStartupLocation.Manual;
            return true;
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
            _lastPreviewMoveAt = now;
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
        private void AccountsList_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(Account))) { e.Handled = true; return; }
            if (_didPreviewReorder)
            {
                var currentOrder = ViewModel.Accounts.ToList();
                bool orderChanged = false;
                if (currentOrder.Count != _orderBeforeDrag.Count) orderChanged = true;
                else
                {
                    for (int i = 0; i < currentOrder.Count; i++) { if (currentOrder[i].Id != _orderBeforeDrag[i].Id) { orderChanged = true; break; } }
                }
                if (orderChanged) { AccountService.SaveAccounts(currentOrder); ViewModel.ShowToast("Account order updated!"); }
            }
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
            ViewModel.IsBusy = true;
            ViewModel.BusyStatus = "Capturing current login...";
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
                    }));
                if (result.Item1) { await ViewModel.LoadAccountsAsync(); ViewModel.ShowToast("Account captured successfully!"); return true; }
                else if (result.Item2 != null) { new CustomMessageBox("Capture Failed", result.Item2, MessageBoxVisualStyle.Error).ShowDialog(); }
                return false;
            }
            finally { ViewModel.IsBusy = false; ViewModel.BusyStatus = ""; }
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
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            var path = value as string;
            if (string.IsNullOrWhiteSpace(path)) return null;

            try
            {
                var fullPath = Path.GetFullPath(path);
                if (!File.Exists(fullPath)) return null;

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.UriSource = new Uri(fullPath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotImplementedException();
    }
}
