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

    public static class WindowsClipboard
    {
        public static void SetText(string text)
        {
            if (text == null) return;
            OpenClipboard();
            NativeMethods.EmptyClipboard();
            IntPtr hGlobal = default;
            try
            {
                var bytes = (text.Length + 1) * 2;
                hGlobal = Marshal.AllocHGlobal(bytes);
                if (hGlobal == default) throw new Win32Exception(Marshal.GetLastWin32Error());
                var target = NativeMethods.GlobalLock(hGlobal);
                if (target == default) throw new Win32Exception(Marshal.GetLastWin32Error());
                try { Marshal.Copy(text.ToCharArray(), 0, target, text.Length); }
                finally { NativeMethods.GlobalUnlock(target); }
                if (NativeMethods.SetClipboardData(13, hGlobal) == default) throw new Win32Exception(Marshal.GetLastWin32Error());
                hGlobal = default;
            }
            finally { if (hGlobal != default) Marshal.FreeHGlobal(hGlobal); NativeMethods.CloseClipboard(); }
        }

        public static void OpenClipboard()
        {
            var num = 10;
            while (true)
            {
                if (NativeMethods.OpenClipboard(default)) break;
                if (--num == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                Thread.Sleep(100);
            }
        }
    }

    public class EventForwarder
    {
        readonly IntPtr _target;
        public EventForwarder(IntPtr target) { _target = target; }
        public void MouseDownDrag() { NativeMethods.ReleaseCapture(); NativeMethods.SendMessage(_target, 0xA1, 0x2, 0); }
        public void MouseResizeDrag(int wParam) { if (wParam == 0) return; NativeMethods.ReleaseCapture(); NativeMethods.SendMessage(_target, 0xA1, wParam, 0); }
        public void WindowAction(int action) { NativeMethods.ReleaseCapture(); if (action != 0x0010) NativeMethods.SendMessage(_target, 0x112, action, 0); else NativeMethods.SendMessage(_target, 0x0010, 0, 0); }
    }

    public static class NativeFuncs
    {
        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        public static bool BringToFront()
        {
            var cur = Process.GetCurrentProcess();
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic-Games_Account_Switcher").ToLower();
            var target = Process.GetProcessesByName("Epic-Games_Account_Switcher").FirstOrDefault(p => p.Id != cur.Id && !string.IsNullOrEmpty(p.MainModule?.FileName) && p.MainModule.FileName.ToLower().StartsWith(dir));
            if (target == null || target.MainWindowHandle == IntPtr.Zero) return false;
            var hwnd = target.MainWindowHandle;
            ShowWindowAsync(hwnd, 5);
            ShowWindowAsync(hwnd, 9);
            NativeMethods.SetForegroundWindow(hwnd);
            return true;
        }

        public static void HideWindow(IntPtr handle) { NativeMethods.SetWindowLong(handle, -20, (NativeMethods.GetWindowLong(handle, -20) | 0x00000080) & ~0x00040000); }
        public static void ShowWindow(IntPtr handle) { NativeMethods.SetWindowLong(handle, -20, (NativeMethods.GetWindowLong(handle, -20) ^ 0x00000080) | 0x00040000); }
    }
}
