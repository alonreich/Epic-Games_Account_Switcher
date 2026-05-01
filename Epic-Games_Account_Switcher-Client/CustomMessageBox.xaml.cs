using System;
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
            HeaderInstruction.Content = title.ToUpper();
            Message.Text = message;
            ButtonOk.Content = primaryBtnText;
            if (string.IsNullOrEmpty(secondaryBtnText)) { ButtonNo.Visibility = Visibility.Collapsed; }
            else { ButtonNo.Content = secondaryBtnText; ButtonNo.Visibility = Visibility.Visible; }
            if (string.IsNullOrEmpty(cancelBtnText)) { ButtonCancel.Visibility = Visibility.Collapsed; }
            else { ButtonCancel.Content = cancelBtnText; ButtonCancel.Visibility = Visibility.Visible; }
            switch (style)
            {
                case MessageBoxVisualStyle.Success: HeaderBg.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9ECE6A")); HeaderInstruction.Foreground = Brushes.Black; break;
                case MessageBoxVisualStyle.Error: HeaderBg.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F7768E")); HeaderInstruction.Foreground = Brushes.Black; break;
                default: HeaderBg.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#14151E")); HeaderInstruction.Foreground = Brushes.White; break;
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
