using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Epic_Games_Account_Switcher_Globals
{
    /// <summary>
    /// Where the Epic Games Launcher window really is on screen, and how to write that into the launcher's
    /// "[WindowSettings] MainLauncherWindow" value. A switch force-stops the launcher, so the launcher itself never gets
    /// the chance to save its window position; the switcher reads it from the live window just before stopping it.
    /// </summary>
    public static class LauncherWindowPlacement
    {
        public sealed class Snapshot
        {
            public int Left, Top, Right, Bottom;
            public bool Maximized;
        }

        /// <summary>The launcher's main window rectangle in screen pixels, or null when it has no usable window (closed, hidden in the notification area, minimized).</summary>
        public static Snapshot Capture()
        {
            try
            {
                var pids = new HashSet<uint>();
                foreach (var p in Process.GetProcessesByName("EpicGamesLauncher"))
                {
                    using (p) { try { pids.Add((uint)p.Id); } catch { } }
                }
                if (pids.Count == 0) return null;

                IntPtr found = IntPtr.Zero;
                long bestArea = 0;
                RECT bestRect = default(RECT);
                EnumWindows((hwnd, l) =>
                {
                    if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
                    uint pid;
                    GetWindowThreadProcessId(hwnd, out pid);
                    if (!pids.Contains(pid)) return true;
                    if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;
                    if ((GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
                    RECT r;
                    if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf(typeof(RECT))) != 0 && !GetWindowRect(hwnd, out r)) return true;
                    long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                    if (area > bestArea) { bestArea = area; found = hwnd; bestRect = r; }
                    return true;
                }, IntPtr.Zero);

                // Smaller than any real launcher window: a splash screen or helper, not something to remember.
                if (found == IntPtr.Zero || bestRect.Right - bestRect.Left < 400 || bestRect.Bottom - bestRect.Top < 300) return null;
                return new Snapshot { Left = bestRect.Left, Top = bestRect.Top, Right = bestRect.Right, Bottom = bestRect.Bottom, Maximized = IsZoomed(found) };
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("[WINDOW] Could not read the launcher window position", ex);
                return null;
            }
        }

        /// <summary>
        /// Returns <paramref name="storedValue"/> with its window rectangle replaced by the live one. The stored value is
        /// returned unchanged when it has an unknown shape or was saved on a scaled display (its units are then not
        /// plain screen pixels), so an uncertain rectangle is never written.
        /// </summary>
        public static string Apply(string storedValue, Snapshot live)
        {
            if (string.IsNullOrWhiteSpace(storedValue) || live == null) return storedValue;
            if (!WindowRectPattern.IsMatch(storedValue)) return storedValue;

            var dpi = DpiPattern.Match(storedValue);
            double scale;
            if (dpi.Success && double.TryParse(dpi.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out scale) && Math.Abs(scale - 1.0) > 0.001) return storedValue;

            var result = storedValue;
            if (live.Maximized)
            {
                // Keep the stored rectangle as the size to restore to; only remember that the window was maximized.
                result = SetFlag(result, "IsMaximised", true);
            }
            else
            {
                int width = live.Right - live.Left, height = live.Bottom - live.Top;
                if (width < 400 || height < 300) return storedValue;
                result = WindowRectPattern.Replace(result, "WindowRect=\"Left=" + F(live.Left) + " Top=" + F(live.Top) + " Right=" + F(live.Right) + " Bottom=" + F(live.Bottom) + "\"", 1);
                result = SizePattern.Replace(result, "CurrentWindowSize=\"X=" + F(width) + " Y=" + F(height) + "\"", 1);
                result = SetFlag(result, "IsMaximised", false);
            }
            return SetFlag(result, "IsMinimised", false);
        }

        private static readonly Regex WindowRectPattern = new Regex("WindowRect=\"Left=[-0-9.]+ Top=[-0-9.]+ Right=[-0-9.]+ Bottom=[-0-9.]+\"", RegexOptions.Compiled);
        private static readonly Regex SizePattern = new Regex("CurrentWindowSize=\"X=[-0-9.]+ Y=[-0-9.]+\"", RegexOptions.Compiled);
        private static readonly Regex DpiPattern = new Regex("ScreenDPI=([0-9.]+)", RegexOptions.Compiled);

        private static string F(int value) => value.ToString("0.000", CultureInfo.InvariantCulture);

        private static string SetFlag(string value, string name, bool on)
        {
            return Regex.Replace(value, name + "=\"(true|false)\"", name + "=\"" + (on ? "true" : "false") + "\"", RegexOptions.IgnoreCase);
        }

        #region Win32

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const uint GW_OWNER = 4;
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        #endregion
    }
}
