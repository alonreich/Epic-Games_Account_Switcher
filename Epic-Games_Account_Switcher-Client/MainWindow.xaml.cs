using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Data;
using System.Threading.Tasks;
using Epic_Games_Account_Switcher_Globals;

namespace Epic_Games_Account_Switcher_Client
{
    public partial class MainWindow : Window
    {
        public MainViewModel ViewModel { get; set; }

        public MainWindow()
        {
            InitializeComponent();
            ViewModel = new MainViewModel();
            DataContext = ViewModel;

            ViewModel.RequestCapture += ViewModel_RequestCapture;
            ViewModel.RequestInstruction += ViewModel_RequestInstruction;
            Loaded += MainWindow_Loaded;

            // Load saved size/position
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

        private async Task Task_RequestInstruction(string title, string message)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                new CustomMessageBox(title, message).ShowDialog();
            });
        }

        private async Task ViewModel_RequestInstruction(string title, string message)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                new CustomMessageBox(title, message).ShowDialog();
            });
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            ViewModel.LoadAccounts();
        }

        private async Task<bool> ViewModel_RequestCapture(MainViewModel.AccountMetadata metadata)
        {
            var captureWin = new CaptureAccountWindow(metadata.Name, metadata.Description, metadata.Email, metadata.ImagePath) { Owner = this };
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
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private async void Capture_Click(object sender, RoutedEventArgs e)
        {
            var detected = AccountService.GetDetectedMetadata();
            var captureWin = new CaptureAccountWindow(detected.Name, "", detected.Email, "pack://application:,,,/Skins_Icons/Default_Skin_Guy.jpg") { Owner = this };
            if (captureWin.ShowDialog() == true)
            {
                await ExecuteCapture(captureWin);
            }
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
                    (status) => 
                    {
                        Dispatcher.Invoke(() => ViewModel.BusyStatus = status);
                    }, async (existingName) => 
                    {
                        return await Dispatcher.InvokeAsync(() => 
                        {
                            var result = MessageBox.Show($"Account '{existingName}' already exists for this Epic ID.\n\nDo you want to overwrite it with the current login data?", "Overwrite Account?", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                            return result == MessageBoxResult.Yes;
                        });
                    });

                if (success)
                {
                    ViewModel.LoadAccounts();
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
