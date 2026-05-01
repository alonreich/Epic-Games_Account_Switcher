using System;
using System.Windows;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Epic_Games_Account_Switcher_Client
{
    public partial class ProgressWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        public ProgressWindow(string title)
        {
            InitializeComponent();
            TitleLabel.Text = title.ToUpper();
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
                    var h = new WindowInteropHelper(this).Handle;
                    if (h != IntPtr.Zero) SetForegroundWindow(h);
                } catch { }
            }), DispatcherPriority.Render);
        }
    }
}
