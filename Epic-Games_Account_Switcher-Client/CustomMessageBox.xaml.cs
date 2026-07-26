using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Input;

namespace Epic_Games_Account_Switcher_Client
{
    public enum MessageBoxVisualStyle { Neutral, Success, Error }
    public partial class CustomMessageBox : Window
    {
        public CustomMessageBox(string title, string message, MessageBoxVisualStyle style = MessageBoxVisualStyle.Neutral) : this(title, message, "OK", null, null, style) { }
        public CustomMessageBox(string title, string message, string primaryBtnText, string secondaryBtnText = null, string cancelBtnText = null, MessageBoxVisualStyle style = MessageBoxVisualStyle.Neutral)
        {
            InitializeComponent();
            ConfigureOwner();
            HeaderInstruction.Content = title.ToUpper();
            Message.Text = message;
            ButtonOk.Content = primaryBtnText;
            if (string.IsNullOrEmpty(secondaryBtnText)) { ButtonNo.Visibility = Visibility.Collapsed; }
            else { ButtonNo.Content = secondaryBtnText; ButtonNo.Visibility = Visibility.Visible; }
            if (string.IsNullOrEmpty(cancelBtnText)) { ButtonCancel.Visibility = Visibility.Collapsed; }
            else { ButtonCancel.Content = cancelBtnText; ButtonCancel.Visibility = Visibility.Visible; }
            ButtonNo.IsCancel = !string.IsNullOrEmpty(secondaryBtnText) && string.IsNullOrEmpty(cancelBtnText);
            ButtonCancel.IsCancel = !string.IsNullOrEmpty(cancelBtnText);
            switch (style)
            {
                case MessageBoxVisualStyle.Success: HeaderBg.Background = (Brush)Application.Current.Resources["SuccessBackground"] ?? new SolidColorBrush(Color.FromRgb(158, 206, 106)); HeaderInstruction.Foreground = Brushes.Black; break;
                case MessageBoxVisualStyle.Error: HeaderBg.Background = (Brush)Application.Current.Resources["WarningForeground"] ?? new SolidColorBrush(Color.FromRgb(247, 118, 142)); HeaderInstruction.Foreground = Brushes.Black; break;
                default: HeaderBg.Background = (Brush)Application.Current.Resources["HeaderbarBackground"] ?? new SolidColorBrush(Color.FromRgb(20, 21, 30)); HeaderInstruction.Foreground = Brushes.White; break;
            }
        }
        private void ConfigureOwner()
        {
            try
            {
                var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => !ReferenceEquals(w, this) && w.IsActive && w.IsVisible);
                if (owner == null && Application.Current.MainWindow != null && !ReferenceEquals(Application.Current.MainWindow, this) && Application.Current.MainWindow.IsVisible)
                    owner = Application.Current.MainWindow;

                if (owner != null)
                {
                    Owner = owner;
                    WindowStartupLocation = WindowStartupLocation.CenterOwner;
                }
                else
                {
                    WindowStartupLocation = WindowStartupLocation.CenterScreen;
                }
            }
            catch
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
        }
        private void DragWindow(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
        private void BtnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void BtnExit(object sender, RoutedEventArgs e) => Close();
        private void ButtonOk_OnClick(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
        private void ButtonNo_OnClick(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
        private void ButtonCancel_OnClick(object sender, RoutedEventArgs e) { DialogResult = null; Close(); }
    }
}
