using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;

namespace Epic_Games_Account_Switcher_Client
{

    public partial class CustomMessageBox : Window
    {
        private void BtnExit(object sender, RoutedEventArgs e) =>
            Close();
        private void BtnMinimize(object sender, RoutedEventArgs e) =>
            WindowHandling.BtnMinimize(sender, e, this);
        private void DragWindow(object sender, MouseButtonEventArgs e) =>
            WindowHandling.DragWindow(sender, e, this);

        public CustomMessageBox(string title, string text)
        {
            InitializeComponent();
            Title = title;
            HeaderInstruction.Content = title;
            Message.Text = text;
        }

        private void ButtonOk_OnClick(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }

    public class WindowHandling
    {
        public static void BtnMinimize(object sender, RoutedEventArgs e, Window window)
        {
            window.WindowState = WindowState.Minimized;
        }

        public static void DragWindow(object sender, MouseButtonEventArgs e, Window window)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                if (e.ClickCount == 2)
                {
                    SwitchState(window);
                }
                else
                {
                    if (window.WindowState == WindowState.Maximized)
                    {
                        var percentHorizontal = e.GetPosition(window).X / window.ActualWidth;
                        var targetHorizontal = window.RestoreBounds.Width * percentHorizontal;

                        var percentVertical = e.GetPosition(window).Y / window.ActualHeight;
                        var targetVertical = window.RestoreBounds.Height * percentVertical;

                        window.WindowState = WindowState.Normal;

                        _ = GetCursorPos(out var lMousePosition);

                        window.Left = lMousePosition.X - targetHorizontal;
                        window.Top = lMousePosition.Y - targetVertical;
                    }

                    window.DragMove();
                }
            }
        }
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out Point lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        public struct Point
        {
            public int X;
            public int Y;
        }
        public static void SwitchState(Window window)
        {
            switch (window.WindowState)
            {
                case WindowState.Normal:
                    window.WindowState = WindowState.Maximized;
                    break;
                case WindowState.Maximized:
                    window.WindowState = WindowState.Normal;
                    break;
            }
        }
    }
}


