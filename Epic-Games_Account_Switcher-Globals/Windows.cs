
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;

namespace Epic_Games_Account_Switcher_Globals
{
    internal static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        public const int GwlExStyle = -20;
        public static readonly int WsExAppWindow = 0x00040000;
        public static readonly int WsExToolWindow = 0x00000080;

        [DllImport("user32")]
        public static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass,
            string lpszWindow);

        [DllImport("user32.dll")]
        public static extern bool GetClientRect(IntPtr hWnd, out Rect lpRect);

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, int wParam, int lParam);
        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        public struct TokenPrivileges
        {
            public uint PrivilegeCount;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
            public LuidAndAttributes[] Privileges;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct LuidAndAttributes
        {
            public Luid Luid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        public readonly struct Luid
        {
            private readonly uint LowPart;
            private readonly int HighPart;
        }

        [Flags]
        public enum ProcessAccessFlags : uint
        {
            All = 0x001F0FFF,

            QueryInformation = 0x00000400,
        }

        public enum SecurityImpersonationLevel
        {
            SecurityImpersonation

        }

        public enum TokenType
        {
            TokenPrimary = 1

        }

        [StructLayout(LayoutKind.Sequential)]
        public readonly struct ProcessInformation
        {
            private readonly IntPtr hProcess;
            private readonly IntPtr hThread;
            private readonly int dwProcessId;
            private readonly int dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public readonly struct StartupInfo
        {
            private readonly int cb;
            private readonly string lpReserved;
            private readonly string lpDesktop;
            private readonly string lpTitle;
            private readonly int dwX;
            private readonly int dwY;
            private readonly int dwXSize;
            private readonly int dwYSize;
            private readonly int dwXCountChars;
            private readonly int dwYCountChars;
            private readonly int dwFillAttribute;
            private readonly int dwFlags;
            private readonly int wShowWindow;
            private readonly int cbReserved2;
            private readonly int lpReserved2;
            private readonly int hStdInput;
            private readonly int hStdOutput;
            private readonly int hStdError;
        }

        [DllImport("kernel32.dll", ExactSpelling = true)]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern bool OpenProcessToken(IntPtr h, int acc, ref IntPtr phtok);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool LookupPrivilegeValue(string host, string name, ref Luid pluid);

        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern bool AdjustTokenPrivileges(IntPtr htok, bool disall, ref TokenPrivileges newst, int len, IntPtr prev, IntPtr relen);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("user32.dll")]
        public static extern IntPtr GetShellWindow();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(ProcessAccessFlags processAccess, bool bInheritHandle, uint processId);

        [DllImport("advapi32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern bool DuplicateTokenEx(IntPtr hExistingToken, uint dwDesiredAccess, IntPtr lpTokenAttributes, SecurityImpersonationLevel impersonationLevel, TokenType tokenType, out IntPtr phNewToken);

        [DllImport("advapi32", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcessWithTokenW(IntPtr hToken, int dwLogonFlags, string lpApplicationName, string lpCommandLine, int dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory, [In] ref StartupInfo lpStartupInfo, out ProcessInformation lpProcessInformation);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalUnlock(IntPtr hMem);

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
        public static extern int AllocConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int FreeConsole();

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool SetWindowText(IntPtr hwnd, string lpString);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AttachConsole(int dwProcessId);

        public const int IldTransparent = 0x00000001;
        public const int IldImage = 0x00000020;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [ComImport]
        [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IImageList
        {
            [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, ref int pi);
            [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, ref int pi);
            [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
            [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
            [PreserveSig] int AddMasked(IntPtr hbmImage, uint crMask, ref int pi);
            [PreserveSig] int Draw(ref IMAGELISTDRAWPARAMS pimldp);
            [PreserveSig] int Remove(int i);
            [PreserveSig] int GetIcon(int i, uint flags, ref IntPtr picon);
            [PreserveSig] int GetImageInfo(int i, ref IMAGEINFO pImageInfo);
            [PreserveSig] int Copy(int iDst, [MarshalAs(UnmanagedType.IUnknown)] object punkSrc, int iSrc, uint uFlags);
            [PreserveSig] int Merge(int i1, [MarshalAs(UnmanagedType.IUnknown)] object punk2, int i2, int dx, int dy, ref Guid riid, ref IntPtr ppv);
            [PreserveSig] int Clone(ref Guid riid, ref IntPtr ppv);
            [PreserveSig] int GetImageCount(ref int pi);
            [PreserveSig] int SetImageCount(uint uNewCount);
            [PreserveSig] int Extract(int i, ref Guid riid, ref IntPtr ppv);
            [PreserveSig] int GetImageListQueueStatus(ref uint lpdwStatus);
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IMAGELISTDRAWPARAMS
        {
            public int cbSize;
            public IntPtr himl;
            public int i;
            public IntPtr hdcDst;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public int xBitmap;
            public int yBitmap;
            public uint rgbBk;
            public uint rgbFg;
            public uint fStyle;
            public uint dwRop;
            public uint fState;
            public uint Frame;
            public uint crEffect;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IMAGEINFO
        {
            public IntPtr hbmImage;
            public IntPtr hbmMask;
            public int Unused1;
            public int Unused2;
            public Rect rcImage;
        }

        [DllImport("shell32.dll", EntryPoint = "#727")]
        public static extern int SHGetImageList(int iImageList, ref Guid riid, ref IImageList ppv);

        [DllImport("user32.dll", EntryPoint = "DestroyIcon", SetLastError = true)]
        public static extern int DestroyIcon(IntPtr hIcon);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SHGetFileInfo(
            string pszPath,
            uint dwFileAttributes,
            ref SHFILEINFO psfi,
            uint cbFileInfo,
            uint uFlags
        );

        [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true, CharSet = CharSet.Unicode)]
        [SuppressUnmanagedCodeSecurity]
        internal delegate bool EnumResNameProc(IntPtr hModule, IntPtr lpszType, IntPtr lpszName, IntPtr lParam);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr FindResource(IntPtr hModule, IntPtr lpName, IntPtr lpType);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr LoadResource(IntPtr hModule, IntPtr hResInfo);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr LockResource(IntPtr hResData);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint SizeofResource(IntPtr hModule, IntPtr hResInfo);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [SuppressUnmanagedCodeSecurity]
        public static extern bool EnumResourceNames(IntPtr hModule, IntPtr lpszType, EnumResNameProc lpEnumFunc, IntPtr lParam);
    }

    public static class WindowsClipboard
    {

        public static void SetText(string text)
        {
            Globals.DebugWriteLine(@"[Func:Data\GenericFunctions.SetText] text=hidden");
            if (text == null) return;
            OpenClipboard();

            _ = NativeMethods.EmptyClipboard();
            IntPtr hGlobal = default;
            try
            {
                var bytes = (text.Length + 1) * 2;
                hGlobal = Marshal.AllocHGlobal(bytes);

                if (hGlobal == default) ThrowWin32();
                var target = NativeMethods.GlobalLock(hGlobal);
                if (target == default) ThrowWin32();

                try
                {
                    Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                }
                finally
                {
                    _ = NativeMethods.GlobalUnlock(target);
                }

                if (NativeMethods.SetClipboardData(CfUnicodeText, hGlobal) == default) ThrowWin32();
                hGlobal = default;
            }
            finally
            {
                if (hGlobal != default) Marshal.FreeHGlobal(hGlobal);
                _ = NativeMethods.CloseClipboard();
            }
        }

        public static void OpenClipboard()
        {
            Globals.DebugWriteLine(@"[Func:Data\GenericFunctions.OpenClipboard]");
            var num = 10;
            while (true)
            {
                if (NativeMethods.OpenClipboard(default)) break;
                if (--num == 0) ThrowWin32();
                Thread.Sleep(100);
            }
        }

        private const uint CfUnicodeText = 13;

        private static void ThrowWin32()
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public class EventForwarder
    {
        public const int WmNclButtonDown = 0xA1;
        public const int WmSysCommand = 0x112;
        public const int HtCaption = 0x2;

        readonly IntPtr _target;

        public EventForwarder(IntPtr target)
        {
            _target = target;
        }

        public void MouseDownDrag()
        {
            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(_target, WmNclButtonDown, HtCaption, 0);
        }

        public void MouseResizeDrag(int wParam)
        {
            if (wParam == 0) return;
            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(_target, WmNclButtonDown, wParam, 0);
        }

        public void WindowAction(int action)
        {
            NativeMethods.ReleaseCapture();
            if (action != 0x0010) NativeMethods.SendMessage(_target, WmSysCommand, action, 0);
            else NativeMethods.SendMessage(_target, 0x0010, 0, 0);
        }

        public void HideWindow()
        {
            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(_target, WmSysCommand, 0xF020, 0);
            NativeFuncs.HideWindow(_target);
        }
    }

    public static class NativeFuncs
    {
        public static int AllocConsole() => NativeMethods.AllocConsole();
        public static int FreeConsole() => NativeMethods.FreeConsole();
        public static void SetWindowText(string text)
        {
            var handle = NativeMethods.GetConsoleWindow();

            _ = NativeMethods.SetWindowText(handle, text);
        }

        public static bool AttachToConsole(int dwProcessId) => NativeMethods.AttachConsole(dwProcessId);

        public static bool BringToFront()
        {

            var proc = Process.GetProcessesByName("Epic-Games_Account_Switcher").FirstOrDefault();
            if (proc == null || proc.MainWindowHandle == IntPtr.Zero) return false;
            const int swRestore = 9;
            var hwnd = proc.MainWindowHandle;
            ShowWindow(hwnd);
            _ = NativeMethods.ShowWindow(hwnd, swRestore);
            _ = NativeMethods.SetForegroundWindow(hwnd);
            return true;
        }

        public static void HideWindow(IntPtr handle)
        {
            _ = NativeMethods.SetWindowLong(handle, NativeMethods.GwlExStyle, (NativeMethods.GetWindowLong(handle, NativeMethods.GwlExStyle) | NativeMethods.WsExToolWindow) & ~NativeMethods.WsExAppWindow);
        }

        public static void ShowWindow(IntPtr handle)
        {
            _ = NativeMethods.SetWindowLong(handle, NativeMethods.GwlExStyle, (NativeMethods.GetWindowLong(handle, NativeMethods.GwlExStyle) ^ NativeMethods.WsExToolWindow) | NativeMethods.WsExAppWindow);
        }

        public static int GetWindow(IntPtr handle) => NativeMethods.GetWindowLong(handle, NativeMethods.GwlExStyle);
    }
}

