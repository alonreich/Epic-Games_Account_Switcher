using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Epic_Games_Account_Switcher_Client
{
    public partial class ProgressWindow : Window
    {
        // Setup log lines start with a technical tag such as "CLEANUP:" or "WARNING:"; the friendly status line hides it.
        private static readonly Regex TagPrefix = new Regex(@"^[A-Z0-9_\-\[\] ]{2,24}:\s*", RegexOptions.Compiled);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        public ProgressWindow(string title)
        {
            InitializeComponent();
            TitleLabel.Text = title.ToUpper();
            Title = title;
            // Bring setup to the front once when it opens; never steal focus again while it runs.
            Loaded += (s, e) => { try { var h = new WindowInteropHelper(this).Handle; SetForegroundWindow(h); Activate(); } catch { } };
        }
        public void UpdateProgress(int val)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (val < 0) val = 0; if (val > 100) val = 100;
                MainProgressBar.Value = val;
                PercentageLabel.Text = val + "%";
            }), DispatcherPriority.Render);
        }
        public void UpdateStatus(string status)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    string time = DateTime.Now.ToString("HH:mm:ss");
                    LogList.Items.Add("[" + time + "] " + status);
                    if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
                    var friendly = TagPrefix.Replace(status ?? "", "");
                    if (!string.IsNullOrWhiteSpace(friendly))
                    {
                        StatusText.Text = friendly;
                        (UIElementAutomationPeer.FromElement(StatusText) ?? UIElementAutomationPeer.CreatePeerForElement(StatusText))?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
                    }
                } catch { }
            }), DispatcherPriority.Render);
        }

        private void DetailsToggle_Click(object sender, RoutedEventArgs e)
        {
            var show = LogList.Visibility != Visibility.Visible;
            LogList.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            DetailsToggle.Content = show ? "HIDE DETAILS" : "SHOW DETAILS";
        }

        private void Window_DragMove(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { }
        }
    }
}
