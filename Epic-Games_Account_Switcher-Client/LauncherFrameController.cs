using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Epic_Games_Account_Switcher_Globals;

namespace Epic_Games_Account_Switcher_Client
{
    /// <summary>
    /// EXPERIMENTAL "magnetic frame": while the Epic Games Launcher window is open (and not minimized/maximized),
    /// the switcher resizes itself into a frame AROUND the launcher and sits directly below it in the z-order, so the
    /// launcher appears inside the switcher with compact profile cards on the left/right edges.
    /// The launcher is never re-parented or modified; we only read its position. When it closes for longer than a few
    /// seconds (an account switch restarts it), the normal layout and the previous window size are restored.
    /// </summary>
    internal sealed class LauncherFrameController : IDisposable
    {
        public const double SideBand = 210;
        public const double TopBand = 74;
        public const double BottomBand = 62;
        private static readonly TimeSpan EnterDelay = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan ExitDelay = TimeSpan.FromSeconds(4);

        private readonly MainWindow _window;
        private readonly DispatcherTimer _timer;
        private bool _active;
        private Rect _savedBounds;
        private WindowState _savedState;
        private double _savedMinWidth, _savedMinHeight;
        private IntPtr _launcherHwnd;
        private DateTime _seenSince = DateTime.MinValue;
        private DateTime _missingSince = DateTime.MinValue;

        public bool IsActive => _active;

        public LauncherFrameController(MainWindow window)
        {
            _window = window;
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (s, e) => Tick();
        }

        public void SetEnabled(bool enabled)
        {
            if (enabled) { if (!_timer.IsEnabled) _timer.Start(); }
            else { _timer.Stop(); Exit(); }
        }

        public void OnActivated()
        {
            if (_active && _launcherHwnd != IntPtr.Zero && IsWindow(_launcherHwnd)) PlaceBelow(_launcherHwnd);
        }

        public void Dispose()
        {
            _timer.Stop();
            Exit();
        }

        private void Tick()
        {
            try
            {
                if (!_window.IsVisible || _window.WindowState == WindowState.Minimized) return;
                RECT rect;
                var hwnd = FindLauncherWindow(out rect);
                bool usable = hwnd != IntPtr.Zero && !IsIconic(hwnd) && !IsZoomed(hwnd);
                var now = DateTime.UtcNow;
                if (usable)
                {
                    _missingSince = DateTime.MinValue;
                    if (_seenSince == DateTime.MinValue) _seenSince = now;
                }
                else
                {
                    _seenSince = DateTime.MinValue;
                    if (_missingSince == DateTime.MinValue) _missingSince = now;
                }

                if (usable && (_active || now - _seenSince >= EnterDelay))
                {
                    if (!Frame(hwnd, rect) && _active) Exit();
                }
                else if (_active && !usable && now - _missingSince >= ExitDelay)
                {
                    Exit();
                }
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("[FRAME] Tick failed; disabling frame for this session", ex);
                _timer.Stop();
                Exit();
            }
        }

        private bool Frame(IntPtr launcher, RECT r)
        {
            var source = PresentationSource.FromVisual(_window);
            if (source?.CompositionTarget == null) return false;
            var m = source.CompositionTarget.TransformFromDevice;
            var tl = m.Transform(new Point(r.Left, r.Top));
            var br = m.Transform(new Point(r.Right, r.Bottom));
            var launcherWidth = br.X - tl.X;
            var launcherHeight = br.Y - tl.Y;
            if (launcherWidth < 400 || launcherHeight < 300) return false;
            if (launcherWidth + 2 * SideBand > SystemParameters.VirtualScreenWidth) return false;

            var target = new Rect(tl.X - SideBand, tl.Y - TopBand, launcherWidth + 2 * SideBand, launcherHeight + TopBand + BottomBand);

            if (!_active)
            {
                _savedState = _window.WindowState;
                if (_window.WindowState != WindowState.Normal) _window.WindowState = WindowState.Normal;
                _savedBounds = new Rect(_window.Left, _window.Top, _window.Width, _window.Height);
                _savedMinWidth = _window.MinWidth;
                _savedMinHeight = _window.MinHeight;
                _window.MinWidth = 0;
                _window.MinHeight = 0;
                _active = true;
                _window.SetFrameLayout(true);
                Globals.WriteToLog("[FRAME] Wrapping around the Epic Games Launcher window.");
            }

            if (Math.Abs(_window.Left - target.X) > 0.5) _window.Left = target.X;
            if (Math.Abs(_window.Top - target.Y) > 0.5) _window.Top = target.Y;
            if (Math.Abs(_window.Width - target.Width) > 0.5) _window.Width = target.Width;
            if (Math.Abs(_window.Height - target.Height) > 0.5) _window.Height = target.Height;
            _launcherHwnd = launcher;
            PlaceBelow(launcher);
            return true;
        }

        private void Exit()
        {
            if (!_active) return;
            _active = false;
            _launcherHwnd = IntPtr.Zero;
            try
            {
                _window.SetFrameLayout(false);
                _window.MinWidth = _savedMinWidth;
                _window.MinHeight = _savedMinHeight;
                _window.Left = _savedBounds.X;
                _window.Top = _savedBounds.Y;
                _window.Width = _savedBounds.Width;
                _window.Height = _savedBounds.Height;
                if (_savedState == WindowState.Maximized) _window.WindowState = WindowState.Maximized;
            }
            catch { }
            Globals.WriteToLog("[FRAME] Launcher closed; restored the normal layout.");
        }

        private void PlaceBelow(IntPtr launcher)
        {
            var me = new WindowInteropHelper(_window).Handle;
            if (me == IntPtr.Zero) return;
            SetWindowPos(me, launcher, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }

        private static IntPtr FindLauncherWindow(out RECT best)
        {
            best = default(RECT);
            var pids = new HashSet<uint>();
            foreach (var p in Process.GetProcessesByName("EpicGamesLauncher"))
            {
                using (p) { try { pids.Add((uint)p.Id); } catch { } }
            }
            if (pids.Count == 0) return IntPtr.Zero;

            IntPtr found = IntPtr.Zero;
            long bestArea = 0;
            RECT bestRect = default(RECT);
            EnumWindows((hwnd, l) =>
            {
                if (!IsWindowVisible(hwnd)) return true;
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (!pids.Contains(pid)) return true;
                if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;
                var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                if ((ex & WS_EX_TOOLWINDOW) != 0) return true;
                RECT r;
                if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf(typeof(RECT))) != 0 && !GetWindowRect(hwnd, out r)) return true;
                long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                if (area > bestArea) { bestArea = area; found = hwnd; bestRect = r; }
                return true;
            }, IntPtr.Zero);
            best = bestRect;
            return found;
        }

        #region Win32

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const uint GW_OWNER = 4;
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        #endregion
    }
}
