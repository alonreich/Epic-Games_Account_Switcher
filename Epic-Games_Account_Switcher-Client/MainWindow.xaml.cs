using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Threading.Tasks;
using Epic_Games_Account_Switcher_Globals;

namespace Epic_Games_Account_Switcher_Client
{
    public partial class MainWindow : Window
    {
        public MainViewModel ViewModel { get; set; }

        private Point _dragStartPoint;
        private DateTime _mouseDownTimestamp;
        private Account _draggedAccount;
        private ListBoxItem _draggedContainer;
        private List<Account> _orderBeforeDrag = new();
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

            
            Width = AppSettings.WindowWidth;
            Height = AppSettings.WindowHeight;

            if (AppSettings.WindowLeft != -1 && AppSettings.WindowTop != -1)
            {
                Left = AppSettings.WindowLeft;
                Top = AppSettings.WindowTop;
                WindowStartupLocation = WindowStartupLocation.Manual;
            }
            else
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            ViewModel.LoadAccounts();
        }

        private async Task<bool> ViewModel_RequestCapture(MainViewModel.AccountMetadata metadata)
        {
            var captureWin = new CaptureAccountWindow(metadata.Name, "", metadata.Email, metadata.ImagePath, "") { Owner = this };
            if (captureWin.ShowDialog() == true)
            {
                return await ExecuteCapture(captureWin);
            }
            return false;
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            AppSettings.WindowWidth = (int)Width;
            AppSettings.WindowHeight = (int)Height;
            AppSettings.WindowLeft = Left;
            AppSettings.WindowTop = Top;
            AppSettings.SaveSettings();
            base.OnClosing(e);
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // Handled by WindowChrome CaptionHeight
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void AccountsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_isDragInProgress)
                return;

            _dragStartPoint = e.GetPosition(AccountsList);
            _mouseDownTimestamp = DateTime.UtcNow;
            _draggedContainer = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            _draggedAccount = _draggedContainer?.DataContext as Account;
        }

        private void AccountsList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_isDragInProgress || e.LeftButton != MouseButtonState.Pressed || _draggedAccount == null || _draggedContainer == null)
                return;

            var current = e.GetPosition(AccountsList);
            var delta = current - _dragStartPoint;

            if (DateTime.UtcNow - _mouseDownTimestamp < TimeSpan.FromMilliseconds(180))
                return;

            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            StartAccountDrag();
        }

        private void StartAccountDrag()
        {
            if (_isDragInProgress || _draggedAccount == null || _draggedContainer == null || AccountsList == null)
                return;

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

                if (effect != DragDropEffects.Move && _didPreviewReorder)
                {
                    RestoreOrderBeforeDrag();
                }
            }
            finally
            {
                if (dragSourceContainer != null)
                    dragSourceContainer.Opacity = 1.0;

                _draggedAccount = null;
                _draggedContainer = null;
                _lastPreviewIndex = -1;
                _isDragInProgress = false;
            }
        }

        private void AccountsList_DragOver(object sender, DragEventArgs e)
        {
            if (!_isDragInProgress || !e.Data.GetDataPresent(typeof(Account)))
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            var dragged = e.Data.GetData(typeof(Account)) as Account;

            if (dragged == null)
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            var now = DateTime.UtcNow;
            if (now - _lastPreviewMoveAt < TimeSpan.FromMilliseconds(55))
            {
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
                return;
            }

            var pointer = e.GetPosition(AccountsList);
            var targetIndex = GetClosestAccountIndex(pointer);
            MoveAccountPreview(dragged, targetIndex);
            _lastPreviewMoveAt = now;

            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }

        private void AccountsList_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(Account)))
            {
                e.Handled = true;
                return;
            }

            if (_didPreviewReorder)
            {
                                                                                           
                var currentOrder = ViewModel.Accounts.ToList();
                bool orderChanged = false;
                if (currentOrder.Count != _orderBeforeDrag.Count) orderChanged = true;
                else
                {
                    for (int i = 0; i < currentOrder.Count; i++)
                    {
                        if (currentOrder[i].Id != _orderBeforeDrag[i].Id)
                        {
                            orderChanged = true;
                            break;
                        }
                    }
                }

                if (orderChanged)
                {
                    AccountService.SaveAccounts(currentOrder);
                    ViewModel.ShowToast("Account order updated!");
                }
            }

            e.Handled = true;
        }

        private void MoveAccountPreview(Account dragged, int toIndex)
        {
            var fromIndex = ViewModel.Accounts.IndexOf(dragged);

            if (fromIndex < 0 || toIndex < 0 || fromIndex == toIndex || toIndex == _lastPreviewIndex)
                return;

            ViewModel.Accounts.Move(fromIndex, toIndex);
            _didPreviewReorder = true;
            _lastPreviewIndex = toIndex;
        }

        private void RestoreOrderBeforeDrag()
        {
            ViewModel.Accounts.Clear();
            foreach (var acc in _orderBeforeDrag)
            {
                ViewModel.Accounts.Add(acc);
            }
        }

        private int GetClosestAccountIndex(Point position)
        {
            if (ViewModel?.Accounts == null || ViewModel.Accounts.Count == 0)
                return -1;

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

                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closestIndex = i;
                }
            }

            return closestIndex;
        }

        private static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T typed)
                    return typed;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private async Task<bool> ExecuteCapture(CaptureAccountWindow captureWin)
        {
            ViewModel.IsBusy = true;
            ViewModel.BusyStatus = "Capturing current login...";
            try
            {
                var success = await AccountService.CaptureCurrentAccount(
                    captureWin.AccountName, 
                    captureWin.AccountEmail, 
                    captureWin.ProfileImagePath, 
                    captureWin.AccountDescription,
                    captureWin.AccountPassword,
                    (status) => 
                    {
                        Dispatcher.Invoke(() => ViewModel.BusyStatus = status);
                    }, async (existingName) => 
                    {
                        return await Dispatcher.InvokeAsync(() => 
                        {
                            var result = new CustomMessageBox("Overwrite Account?", $"Account '{existingName}' already exists for this Epic ID.\n\nDo you want to overwrite it with the current login data?", "Overwrite", "Cancel").ShowDialog();
                            return result == true;
                        });
                    });

                if (success)
                {
                    await ViewModel.LoadAccountsAsync();
                    ViewModel.ShowToast($"Account '{captureWin.AccountName}' captured successfully!");
                    return true;
                }
                return false;
            }
            finally
            {
                ViewModel.IsBusy = false;
                ViewModel.BusyStatus = "";
            }
        }
    }

    public class AnimatedWrapPanel : WrapPanel
    {
        private readonly Dictionary<UIElement, Rect> _lastLayoutSlots = new();

        protected override Size ArrangeOverride(Size finalSize)
        {
            var arrangedSize = base.ArrangeOverride(finalSize);
            var currentSlots = new Dictionary<UIElement, Rect>();

            foreach (UIElement child in InternalChildren)
            {
                if (child is not FrameworkElement frameworkChild) continue;

                var currentSlot = LayoutInformation.GetLayoutSlot(frameworkChild);
                currentSlots[child] = currentSlot;

                if (_lastLayoutSlots.TryGetValue(child, out var previousSlot))
                {
                    var offsetX = previousSlot.Left - currentSlot.Left;
                    var offsetY = previousSlot.Top - currentSlot.Top;

                    if (Math.Abs(offsetX) > 0.1 || Math.Abs(offsetY) > 0.1)
                    {
                        if (frameworkChild.Opacity < 0.99)
                            continue;

                        if (child.RenderTransform is not TranslateTransform transform)
                        {
                            transform = new TranslateTransform();
                            child.RenderTransform = transform;
                        }

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
            foreach (var pair in currentSlots)
            {
                _lastLayoutSlots[pair.Key] = pair.Value;
            }

            return arrangedSize;
        }
    }

    public class BooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return (value is bool b && b) ? Visibility.Visible : Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotImplementedException();
    }

    public class InverseBooleanToEnabledConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return (value is bool b) ? !b : true;
        }
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotImplementedException();
    }
}
