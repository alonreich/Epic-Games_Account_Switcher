using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// The launcher is never re-parented. Its position is followed through Windows window events (no polling lag),
    /// and dragging or resizing the frame moves/resizes the launcher with it, so both behave as one window.
    /// When it closes for longer than a few seconds (an account switch restarts it), the normal layout and the
    /// previous window size are restored. Entering and leaving fade/slide unless Windows animations are turned off.
    /// </summary>
    internal sealed class LauncherFrameController : IDisposable
    {
        public const double SideBand = 210;
        public const double TopBand = 74;
        public const double BottomBand = 62;
        private const double MinLauncherWidth = 400;
        private const double MinLauncherHeight = 300;
        private static readonly TimeSpan EnterDelay = TimeSpan.FromMilliseconds(300);
        private static readonly TimeSpan ExitDelay = TimeSpan.FromSeconds(4);
        private static readonly TimeSpan FadeOutDuration = TimeSpan.FromMilliseconds(160);

        private readonly MainWindow _window;
        private readonly DispatcherTimer _timer;
        private readonly WinEventProc _winEventProc;
        private IntPtr _showHook;
        private IntPtr _launcherHook;
        private uint _launcherHookPid;
        private HwndSource _source;
        private bool _tickQueued;
        private bool _userMoving;
        private bool _transitioning;
        private bool _active;
        private RECT _savedPixelBounds;
        private WindowState _savedState;
        private double _savedMinWidth, _savedMinHeight;
        private IntPtr _launcherHwnd;
        private DateTime _seenSince = DateTime.MinValue;
        private DateTime _missingSince = DateTime.MinValue;

        public bool IsActive => _active;

        public LauncherFrameController(MainWindow window)
        {
            _window = window;
            _winEventProc = OnWinEvent;
            // Fallback only: window events normally drive the frame instantly.
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (s, e) => Tick();
        }

        public void SetEnabled(bool enabled)
        {
            if (enabled)
            {
                AttachWindowHook();
                if (_showHook == IntPtr.Zero)
                    _showHook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
                if (!_timer.IsEnabled) _timer.Start();
                QueueTick();
            }
            else
            {
                _timer.Stop();
                RemoveHooks();
                Exit(true);
            }
        }

        public void OnActivated()
        {
            if (_active && _launcherHwnd != IntPtr.Zero && IsWindow(_launcherHwnd)) PlaceBelow(_launcherHwnd);
        }

        public void Dispose()
        {
            _timer.Stop();
            RemoveHooks();
            Exit(false);
            if (_source != null) { _source.RemoveHook(WndProc); _source = null; }
        }

        private void RemoveHooks()
        {
            if (_showHook != IntPtr.Zero) { UnhookWinEvent(_showHook); _showHook = IntPtr.Zero; }
            RemoveLauncherHook();
        }

        private void RemoveLauncherHook()
        {
            if (_launcherHook != IntPtr.Zero) { UnhookWinEvent(_launcherHook); _launcherHook = IntPtr.Zero; }
            _launcherHookPid = 0;
        }

        /// <summary>Listens to the launcher's own window events (move, resize, minimize, hide, close) only.</summary>
        private void EnsureLauncherHook(IntPtr launcher)
        {
            GetWindowThreadProcessId(launcher, out uint pid);
            if (pid == 0 || (pid == _launcherHookPid && _launcherHook != IntPtr.Zero)) return;
            RemoveLauncherHook();
            _launcherHook = SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _winEventProc, pid, 0, WINEVENT_OUTOFCONTEXT);
            if (_launcherHook != IntPtr.Zero) _launcherHookPid = pid;
        }

        private void AttachWindowHook()
        {
            if (_source != null) return;
            var handle = new WindowInteropHelper(_window).Handle;
            if (handle == IntPtr.Zero) return;
            _source = HwndSource.FromHwnd(handle);
            _source?.AddHook(WndProc);
        }

        /// <summary>Coalesces bursts of window events into one check on the UI thread.</summary>
        private void QueueTick()
        {
            if (_tickQueued) return;
            _tickQueued = true;
            _window.Dispatcher.BeginInvoke(new Action(() => { _tickQueued = false; Tick(); }), DispatcherPriority.Normal);
        }

        private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            try
            {
                if (idObject != OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero) return;
                if (hook == _launcherHook)
                {
                    if (hwnd != _launcherHwnd) { if (eventType == EVENT_OBJECT_SHOW) QueueTick(); return; }
                    if (eventType == EVENT_OBJECT_LOCATIONCHANGE && _active && !_userMoving && !_transitioning)
                    {
                        FollowLauncher();
                        return;
                    }
                    if (eventType == EVENT_OBJECT_LOCATIONCHANGE || eventType == EVENT_OBJECT_SHOW || eventType == EVENT_OBJECT_HIDE ||
                        eventType == EVENT_OBJECT_DESTROY || eventType == EVENT_SYSTEM_MINIMIZESTART || eventType == EVENT_SYSTEM_MINIMIZEEND)
                        QueueTick();
                    return;
                }
                // Global "a window appeared": only top-level windows can be the launcher.
                if (GetAncestor(hwnd, GA_ROOT) == hwnd) QueueTick();
            }
            catch (Exception ex) { Globals.WriteToLog("[FRAME] Window event handling failed", ex); }
        }

        /// <summary>Instant follow while the user drags or resizes the launcher itself (no process enumeration).</summary>
        private void FollowLauncher()
        {
            if (!IsWindow(_launcherHwnd) || IsIconic(_launcherHwnd) || IsZoomed(_launcherHwnd)) { QueueTick(); return; }
            if (!GetVisibleBounds(_launcherHwnd, out RECT rect)) { QueueTick(); return; }
            if (!Frame(_launcherHwnd, rect)) QueueTick();
        }

        private void Tick()
        {
            try
            {
                if (_transitioning || _userMoving) return;
                if (!_window.IsVisible || _window.WindowState == WindowState.Minimized) return;
                RECT rect;
                var hwnd = FindLauncherWindow(out rect);
                bool usable = hwnd != IntPtr.Zero && !IsIconic(hwnd) && !IsZoomed(hwnd);
                var now = DateTime.UtcNow;
                if (usable)
                {
                    _missingSince = DateTime.MinValue;
                    if (_seenSince == DateTime.MinValue) _seenSince = now;
                    EnsureLauncherHook(hwnd);
                }
                else
                {
                    _seenSince = DateTime.MinValue;
                    if (_missingSince == DateTime.MinValue) _missingSince = now;
                }

                if (usable && (_active || now - _seenSince >= EnterDelay))
                {
                    if (!Frame(hwnd, rect) && _active) Exit(true);
                }
                else if (_active && !usable && now - _missingSince >= ExitDelay)
                {
                    Exit(true);
                }
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("[FRAME] Tick failed; disabling frame for this session", ex);
                _timer.Stop();
                RemoveHooks();
                Exit(false);
            }
        }

        private bool TryGetScale(out double scaleX, out double scaleY)
        {
            scaleX = scaleY = 1.0;
            var source = PresentationSource.FromVisual(_window);
            if (source?.CompositionTarget == null) return false;
            scaleX = source.CompositionTarget.TransformToDevice.M11;
            scaleY = source.CompositionTarget.TransformToDevice.M22;
            return true;
        }

        private bool Frame(IntPtr launcher, RECT r)
        {
            if (!TryGetScale(out double sx, out double sy)) return false;
            var launcherWidth = r.Right - r.Left;
            var launcherHeight = r.Bottom - r.Top;
            if (launcherWidth / sx < MinLauncherWidth || launcherHeight / sy < MinLauncherHeight) return false;
            if (launcherWidth / sx + 2 * SideBand > SystemParameters.VirtualScreenWidth) return false;

            int side = (int)Math.Round(SideBand * sx), top = (int)Math.Round(TopBand * sy), bottom = (int)Math.Round(BottomBand * sy);
            var me = new WindowInteropHelper(_window).Handle;
            if (me == IntPtr.Zero) return false;

            bool entering = !_active;
            if (entering)
            {
                _savedState = _window.WindowState;
                if (_window.WindowState != WindowState.Normal) _window.WindowState = WindowState.Normal;
                GetWindowRect(me, out _savedPixelBounds);
                _savedMinWidth = _window.MinWidth;
                _savedMinHeight = _window.MinHeight;
                _window.MinWidth = MinLauncherWidth + 2 * SideBand;
                _window.MinHeight = MinLauncherHeight + TopBand + BottomBand;
                _active = true;
                if (MainWindow.AnimationsEnabled) _window.Opacity = 0;
                _window.SetFrameLayout(true);
                Globals.WriteToLog("[FRAME] Wrapping around the Epic Games Launcher window.");
            }

            _launcherHwnd = launcher;
            EnsureLauncherHook(launcher);
            // One call moves, sizes and stacks the frame directly below the launcher: no intermediate jumps.
            SetWindowPos(me, launcher, r.Left - side, r.Top - top, launcherWidth + 2 * side, launcherHeight + top + bottom, SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            if (entering) _window.PlayFrameEnter();
            return true;
        }

        private void Exit(bool animate)
        {
            if (!_active || _transitioning) return;
            _launcherHwnd = IntPtr.Zero;
            RemoveLauncherHook();
            _userMoving = false;

            if (animate && MainWindow.AnimationsEnabled && _window.IsVisible)
            {
                _transitioning = true;
                _window.FadeWindow(0, FadeOutDuration, () =>
                {
                    RestoreNormalLayout();
                    _transitioning = false;
                    _window.FadeWindow(1, MainWindow.FadeInDuration, null);
                });
                return;
            }
            RestoreNormalLayout();
            _window.Opacity = 1;
        }

        private void RestoreNormalLayout()
        {
            _active = false;
            try
            {
                _window.SetFrameLayout(false);
                _window.MinWidth = _savedMinWidth;
                _window.MinHeight = _savedMinHeight;
                var me = new WindowInteropHelper(_window).Handle;
                var b = _savedPixelBounds;
                if (me != IntPtr.Zero && b.Right > b.Left && b.Bottom > b.Top)
                    SetWindowPos(me, IntPtr.Zero, b.Left, b.Top, b.Right - b.Left, b.Bottom - b.Top, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                if (_savedState == WindowState.Maximized) _window.WindowState = WindowState.Maximized;
            }
            catch (Exception ex) { Globals.WriteToLog("[FRAME] Could not restore the normal layout", ex); }
            Globals.WriteToLog("[FRAME] Launcher closed; restored the normal layout.");
        }

        /// <summary>Moving or resizing the frame moves/resizes the launcher inside it, so both act as one window.</summary>
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (!_active || _transitioning) return IntPtr.Zero;
            try
            {
                switch (msg)
                {
                    case WM_ENTERSIZEMOVE:
                        _userMoving = true;
                        break;
                    case WM_EXITSIZEMOVE:
                        _userMoving = false;
                        QueueTick();
                        break;
                    case WM_MOVING:
                    case WM_SIZING:
                        if (_launcherHwnd != IntPtr.Zero && IsWindow(_launcherHwnd))
                            MoveLauncherInto((RECT)Marshal.PtrToStructure(lParam, typeof(RECT)), msg == WM_MOVING);
                        break;
                }
            }
            catch (Exception ex) { Globals.WriteToLog("[FRAME] Could not move the launcher with the frame", ex); }
            return IntPtr.Zero;
        }

        private void MoveLauncherInto(RECT frame, bool moveOnly)
        {
            if (!TryGetScale(out double sx, out double sy)) return;
            int side = (int)Math.Round(SideBand * sx), top = (int)Math.Round(TopBand * sy), bottom = (int)Math.Round(BottomBand * sy);
            // SetWindowPos uses the full window rect, which includes invisible resize borders around the visible bounds.
            if (!GetWindowRect(_launcherHwnd, out RECT outer) || !GetVisibleBounds(_launcherHwnd, out RECT visible)) return;
            int left = frame.Left + side - (visible.Left - outer.Left);
            int topPx = frame.Top + top - (visible.Top - outer.Top);
            int width = (frame.Right - frame.Left - 2 * side) + (visible.Left - outer.Left) + (outer.Right - visible.Right);
            int height = (frame.Bottom - frame.Top - top - bottom) + (visible.Top - outer.Top) + (outer.Bottom - visible.Bottom);
            var flags = SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_ASYNCWINDOWPOS;
            if (moveOnly) flags |= SWP_NOSIZE;
            SetWindowPos(_launcherHwnd, IntPtr.Zero, left, topPx, width, height, flags);
        }

        private void PlaceBelow(IntPtr launcher)
        {
            var me = new WindowInteropHelper(_window).Handle;
            if (me == IntPtr.Zero) return;
            SetWindowPos(me, launcher, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }

        private static bool GetVisibleBounds(IntPtr hwnd, out RECT r)
        {
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf(typeof(RECT))) == 0) return true;
            return GetWindowRect(hwnd, out r);
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
                if (!GetVisibleBounds(hwnd, out r)) return true;
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
        private delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200, SWP_ASYNCWINDOWPOS = 0x4000;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const uint GW_OWNER = 4;
        private const uint GA_ROOT = 2;
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        private const int OBJID_WINDOW = 0;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000, WINEVENT_SKIPOWNPROCESS = 0x0002;
        private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016, EVENT_SYSTEM_MINIMIZEEND = 0x0017;
        private const uint EVENT_OBJECT_DESTROY = 0x8001, EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_HIDE = 0x8003, EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
        private const int WM_SIZING = 0x0214, WM_MOVING = 0x0216, WM_ENTERSIZEMOVE = 0x0231, WM_EXITSIZEMOVE = 0x0232;

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        #endregion
    }
}
