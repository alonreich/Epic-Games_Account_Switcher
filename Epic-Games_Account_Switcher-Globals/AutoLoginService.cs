using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Collections.Generic;
using System.Text;

namespace Epic_Games_Account_Switcher_Globals
{
    public class AutoLoginService
    {
        private static CancellationTokenSource _cts;
        private static bool _isRunning;
        private static string AnchorPath
        {
            get
            {
                var paths = new[]
                {
                    Path.Combine(AppContext.BaseDirectory, "OpenCV_Anchors", "sign_in_anchor.png"),
                    Path.Combine(Globals.UserDataFolder, "OpenCV_Anchors", "sign_in_anchor.png"),
                    Path.Combine(AppContext.BaseDirectory, "sign_in_anchor.png")
                };
                foreach (var p in paths) if (File.Exists(p)) return p;
                return paths[0];
            }
        }

        public static void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            _cts = new CancellationTokenSource();
            Task.Run(() => MonitorLoop(_cts.Token));
            Globals.WriteToLog("[AUTOLOGIN] Service started.");
        }

        public static void Stop()
        {
            _isRunning = false;
            _cts?.Cancel();
            Globals.WriteToLog("[AUTOLOGIN] Service stopped.");
        }

        private static async Task MonitorLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (Globals.IsProcessRunning("EpicGamesLauncher") || Globals.IsProcessRunning("Epic Games Launcher"))
                    {
                        await CheckAndPerformLogin();
                    }
                }
                catch (Exception ex)
                {
                    Globals.WriteToLog("[AUTOLOGIN] Loop error", ex);
                }
                // Faster polling for better responsiveness
                await Task.Delay(800, token); 
            }
        }

        public static async Task CheckAndPerformLogin()
        {
            var hwnds = FindLauncherWindows();
            if (!hwnds.Any()) return;

            foreach (var hwnd in hwnds)
            {
                using var screenshot = CaptureWindow(hwnd);
                if (screenshot == null) continue;

                if (!File.Exists(AnchorPath)) return;
                
                using var matScreenRaw = screenshot.ToMat();
                using var matScreen = new Mat();
                if (matScreenRaw.Channels() == 4) Cv2.CvtColor(matScreenRaw, matScreen, ColorConversionCodes.BGRA2BGR);
                else matScreenRaw.CopyTo(matScreen);

                using var matAnchor = new Mat(AnchorPath, ImreadModes.Color);
                using var res = new Mat();
                Cv2.MatchTemplate(matScreen, matAnchor, res, TemplateMatchModes.CCoeffNormed);
                Cv2.MinMaxLoc(res, out _, out double maxVal, out _, out OpenCvSharp.Point maxLoc);

                        // STAGE 1: EMAIL PAGE DETECTION
                        if (maxVal > 0.8) 
                        {
                            var accounts = AccountService.GetAccounts();
                            var active = accounts.FirstOrDefault(a => a.IsActive);
                            
                            if (active != null && !string.IsNullOrEmpty(active.Email))
                            {
                                Globals.WriteToLog($"[AUTOLOGIN] STAGE 1: Email Screen. Target: {MaskEmail(active.Email)}");
                                NativeMethods.SetForegroundWindow(hwnd);
                                
                                int inputX = maxLoc.X + (matAnchor.Width / 2);
                                int inputY = maxLoc.Y + matAnchor.Height + 65; 

                                InputSimulator.ClickAt(hwnd, inputX, inputY);
                                await Task.Delay(50);
                                InputSimulator.ClickAt(hwnd, inputX, inputY); 
                                await Task.Delay(100);
                                
                                // Clean the box
                                InputSimulator.KeyPress(InputSimulator.VirtualKey.CONTROL, InputSimulator.VirtualKey.VK_A);
                                InputSimulator.KeyPress(InputSimulator.VirtualKey.BACK);
                                await Task.Delay(50);

                                // Use Typing instead of Paste to prevent clipboard hijacking
                                InputSimulator.TypeText(active.Email);
                                await Task.Delay(100);
                                InputSimulator.KeyPress(InputSimulator.VirtualKey.RETURN);
                                
                                Globals.WriteToLog("[AUTOLOGIN] Email submitted via virtual typing.");
                                for(int i=0; i<15; i++) 
                                {
                                    await Task.Delay(400); // Slightly slower polling for stability
                                    if (await TryPasswordInjection(hwnd, active, matAnchor)) return;
                                }
                                return;
                            }
                        }
                
                // STAGE 2: PASSWORD PAGE DETECTION (Direct match)
                if (maxVal > 0.4 && maxVal < 0.75)
                {
                    var accounts = AccountService.GetAccounts();
                    var active = accounts.FirstOrDefault(a => a.IsActive);
                    if (active != null) await TryPasswordInjection(hwnd, active, matAnchor);
                }
            }
        }

        private static async Task<bool> TryPasswordInjection(IntPtr hwnd, Account active, Mat matAnchor)
        {
            if (string.IsNullOrEmpty(active.Password)) return false;

            using var screenshot = CaptureWindow(hwnd);
            if (screenshot == null) return false;

            using var matScreenRaw = screenshot.ToMat();
            using var matScreen = new Mat();
            if (matScreenRaw.Channels() == 4) Cv2.CvtColor(matScreenRaw, matScreen, ColorConversionCodes.BGRA2BGR);
            else matScreenRaw.CopyTo(matScreen);

            using var res = new Mat();
            Cv2.MatchTemplate(matScreen, matAnchor, res, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(res, out _, out double maxVal, out _, out OpenCvSharp.Point maxLoc);

            if (maxVal > 0.4 && maxVal < 0.75)
            {
                Globals.WriteToLog("[AUTOLOGIN] STAGE 2 (Fast): Password Screen Detected.");
                NativeMethods.SetForegroundWindow(hwnd);

                int inputX = maxLoc.X + (matAnchor.Width / 2);
                int inputY = maxLoc.Y + matAnchor.Height + 65;

                InputSimulator.ClickAt(hwnd, inputX, inputY);
                await Task.Delay(100);
                
                // Use Typing instead of Paste for passwords
                InputSimulator.TypeText(active.Password);
                await Task.Delay(100);
                InputSimulator.KeyPress(InputSimulator.VirtualKey.RETURN);
                
                Globals.WriteToLog("[AUTOLOGIN] Injection complete.");
                await Task.Delay(5000); 
                return true;
            }
            return false;
        }

        private static string MaskEmail(string email)
        {
            if (string.IsNullOrEmpty(email)) return "N/A";
            var parts = email.Split('@');
            if (parts.Length != 2) return "****";
            string name = parts[0];
            if (name.Length > 3) return name.Substring(0, 3) + "***@" + parts[1];
            return "***@" + parts[1];
        }

        private static List<IntPtr> FindLauncherWindows()
        {
            var results = new List<IntPtr>();
            NativeMethods.EnumWindows((hwnd, lParam) =>
            {
                if (NativeMethods.IsWindowVisible(hwnd))
                {
                    var sb = new StringBuilder(256);
                    NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
                    var title = sb.ToString();

                    if (title.Contains("Epic Games Launcher") || title.Equals("Epic Games", StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(hwnd);
                    }
                }
                return true;
            }, IntPtr.Zero);
            return results;
        }

        private static Bitmap CaptureWindow(IntPtr hwnd)
        {
            if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return null;
            int width = rect.right - rect.left;
            int height = rect.bottom - rect.top;

            if (width <= 0 || height <= 0) return null;

            // Try PrintWindow first
            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bmp))
            {
                IntPtr hdc = graphics.GetHdc();
                try { NativeMethods.PrintWindow(hwnd, hdc, 0); }
                catch { }
                finally { graphics.ReleaseHdc(hdc); }
            }

            // Verify if PrintWindow worked (not all black)
            if (IsBitmapBlank(bmp))
            {
                // Fallback: Desktop capture (requires window to be on top)
                try
                {
                    using var g = Graphics.FromImage(bmp);
                    g.CopyFromScreen(rect.left, rect.top, 0, 0, new System.Drawing.Size(width, height), CopyPixelOperation.SourceCopy);
                }
                catch { }
            }

            return bmp;
        }

        private static bool IsBitmapBlank(Bitmap bmp)
        {
            // Simplified check: check a few pixels
            var c1 = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
            var c2 = bmp.GetPixel(10, 10);
            return c1.A == 0 || (c1.R == 0 && c1.G == 0 && c1.B == 0 && c2.R == 0 && c2.G == 0 && c2.B == 0);
        }
    }

    internal static class InputSimulator
    {
        [DllImport("user32.dll")]
        static extern void mouse_event(int dwFlags, int dx, int dy, int dwData, int dwExtraInfo);

        [DllImport("user32.dll")]
        static extern bool ClientToScreen(IntPtr hWnd, ref System.Drawing.Point lpPoint);

        [DllImport("user32.dll")]
        static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

        public enum VirtualKey : byte
        {
            CONTROL = 0x11,
            VK_A = 0x41,
            BACK = 0x08,
            TAB = 0x09,
            RETURN = 0x0D
        }

        [DllImport("user32.dll")]
        static extern bool SetCursorPos(int x, int y);

        public static void ClickAt(IntPtr hwnd, int x, int y)
        {
            var p = new System.Drawing.Point(x, y);
            ClientToScreen(hwnd, ref p);
            
            SetCursorPos(p.X, p.Y);
            Thread.Sleep(50);
            mouse_event(0x02, 0, 0, 0, 0); // Left Down
            Thread.Sleep(50);
            mouse_event(0x04, 0, 0, 0, 0); // Left Up
        }

        public static void KeyPress(params VirtualKey[] keys)
        {
            foreach (var k in keys) keybd_event((byte)k, 0, 0, 0);
            Thread.Sleep(50);
            foreach (var k in keys.Reverse()) keybd_event((byte)k, 0, 2, 0);
        }

        public static void PasteText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            
            var thread = new Thread(() =>
            {
                try
                {
                    // Use WPF Clipboard which is more reliable in this context
                    System.Windows.Clipboard.SetText(text);
                }
                catch { }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            KeyPress(VirtualKey.CONTROL, (VirtualKey)0x56); // VK_V
        }

        public static void TypeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (char c in text)
            {
                short vk = VkKeyScan(c);
                if (vk == -1) continue;
                
                byte shiftState = (byte)((vk >> 8) & 0xff);
                byte key = (byte)(vk & 0xff);

                if ((shiftState & 1) != 0) keybd_event(0x10, 0, 0, 0); // SHIFT down
                
                keybd_event(key, 0, 0, 0);
                keybd_event(key, 0, 2, 0);

                if ((shiftState & 1) != 0) keybd_event(0x10, 0, 2, 0); // SHIFT up
                Thread.Sleep(10);
            }
        }

        [DllImport("user32.dll")]
        static extern short VkKeyScan(char ch);
    }
}
