using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using System.Text;

namespace Epic_Games_Account_Switcher_Globals
{
    internal static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        public const int GwlExStyle = -20;
        public static readonly int WsExAppWindow = 0x00040000;
        public static readonly int WsExToolWindow = 0x00000080;
        [DllImport("user32")]
        public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, int wParam, int lParam);
        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenClipboard(IntPtr hWndNewOwner);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseClipboard();
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetClipboardData(uint uFormat, IntPtr data);
        [DllImport("user32.dll")]
        public static extern bool EmptyClipboard();
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GlobalLock(IntPtr hMem);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalUnlock(IntPtr hMem);
    }

    public static class NativeFuncs
    {
        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        public static bool BringToFront()
        {
            using (var cur = Process.GetCurrentProcess())
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic-Games_Account_Switcher").ToLower();
                var procs = Process.GetProcessesByName("Epic-Games_Account_Switcher");
                try
                {
                    var target = procs.FirstOrDefault(p => p.Id != cur.Id && !string.IsNullOrEmpty(p.MainModule?.FileName) && p.MainModule.FileName.ToLower().StartsWith(dir));
                    if (target == null || target.MainWindowHandle == IntPtr.Zero) return false;
                    var hwnd = target.MainWindowHandle;
                    ShowWindowAsync(hwnd, 5);
                    ShowWindowAsync(hwnd, 9);
                    NativeMethods.SetForegroundWindow(hwnd);
                    return true;
                }
                finally
                {
                    foreach (var p in procs) try { p.Dispose(); } catch { }
                }
            }
        }
    }
}
