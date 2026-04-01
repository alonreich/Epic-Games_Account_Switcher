using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Epic_Games_Account_Switcher_Globals;
using System.Diagnostics;

namespace Epic_Games_Account_Switcher_Client
{
    public partial class App
    {
        public static string StartPage = "";
        private static readonly Mutex Mutex = new(true, "{A240C23D-6F45-4E92-9979-11E6CE10A22C}");

        protected override void OnExit(ExitEventArgs e)
        {
            try { Mutex.ReleaseMutex(); } catch { }
        }

        [STAThread]
        protected override void OnStartup(StartupEventArgs e)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, ev) => {
                try { File.WriteAllText(Path.Combine(Globals.UserDataFolder, "Crash.txt"), ev.ExceptionObject.ToString()); } catch { }
            };

            // Initialize UI Resources
            Resources["HeaderbarBackground"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#14151E"));
            Resources["HeaderbarForeground"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#14151E"));
            Resources["MainBackground"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#00000000"));
            Resources["MessageForeground"] = System.Windows.Media.Brushes.White;
            Resources["IconFill"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F7768E"));
            Resources["BtnBackground"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#363952"));
            Resources["BtnBackgroundHover"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#414868"));
            Resources["BtnBackgroundActive"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#24283B"));
            Resources["BtnForeground"] = System.Windows.Media.Brushes.White;
            Resources["BtnBorder"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#414868"));
            Resources["BtnBorderHover"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#7AA2F7"));
            Resources["BtnBorderActive"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#7AA2F7"));
            Resources["MinBackground"] = System.Windows.Media.Brushes.Transparent;
            Resources["MinBackgroundHover"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2F334D"));
            Resources["MinBackgroundActive"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#24283B"));
            Resources["CloseBackgroundHover"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F7768E"));
            Resources["CloseBackgroundActive"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#C53B53"));

            if (e.Args.Contains("--uninstall")) 
            {
                PerformThoroughUninstall(e.Args.Contains("--confirmed")); 
                return; 
            }

            // Improve portable experience: don't nag if running from a non-program files folder that the user likely chose
            string currentDir = AppContext.BaseDirectory.TrimEnd('\\');
            bool inDownloads = currentDir.Contains("Downloads", StringComparison.OrdinalIgnoreCase);
            bool inDesktop = currentDir.Contains("Desktop", StringComparison.OrdinalIgnoreCase);
            bool inUserFolder = currentDir.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase);
            
            bool shouldNagInstall = !Globals.InstalledToProgramFiles() && 
                                    !e.Args.Contains("--portable") && 
                                    !File.Exists(Path.Combine(AppContext.BaseDirectory, ".portable")) &&
                                    !inDownloads && !inDesktop;

            if (shouldNagInstall) { PerformSmartInstall(); return; }

            Globals.CreateDataFolder(false);
            Directory.SetCurrentDirectory(Globals.UserDataFolder);

            IsRunningAlready();

            try { new MainWindow().Show(); }
            catch (Exception ex) { 
                try { File.WriteAllText(Path.Combine(Globals.UserDataFolder, "LaunchError.txt"), ex.ToString()); } catch { }
                Environment.Exit(1); 
            }
        }

        public static void IsRunningAlready()
        {
            try { if (!Mutex.WaitOne(TimeSpan.Zero, true)) { NativeFuncs.BringToFront(); Environment.Exit(1056); } }
            catch (AbandonedMutexException) { }
        }

        private void PerformSmartInstall()
        {
            if (!Environment.GetCommandLineArgs().Contains("--installing"))
            {
                var result = MessageBox.Show("Install Epic-Games_Account_Switcher to Program Files?\n\nSelecting 'No' will enable Portable Mode and this message won't appear again.", "Install", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.No)
                {
                    // Create a marker file to prevent future nagging
                    try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, ".portable"), ""); } catch { }
                    Process.Start(new ProcessStartInfo { FileName = Environment.ProcessPath, Arguments = "--portable", UseShellExecute = true });
                    try { Mutex.ReleaseMutex(); } catch { }
                    Environment.Exit(0);
                    return;
                }

                if (!Globals.IsAdministrator)
                {
                    try 
                    { 
                        Process.Start(new ProcessStartInfo { FileName = Environment.ProcessPath, Arguments = "--installing", UseShellExecute = true, Verb = "runas" }); 
                        try { Mutex.ReleaseMutex(); } catch { }
                        Environment.Exit(0); 
                    } catch { return; }
                }
            }

            try
            {
                Globals.KillProcess("Epic-Games_Account_Switcher", true);

                var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var targetDir = Path.Combine(programFiles, "Epic-Games_Account_Switcher");
                
                if (Directory.Exists(targetDir))
                {
                    try { Globals.ForceDeleteDirectory(targetDir); } catch { }
                }
                Directory.CreateDirectory(targetDir);
                
                var targetExePath = Path.Combine(targetDir, "Epic-Games_Account_Switcher.exe");
                
                Globals.CopyFilesRecursive(AppDomain.CurrentDomain.BaseDirectory, targetDir, true);
                if (File.Exists(Environment.ProcessPath)) File.Copy(Environment.ProcessPath, targetExePath, true);

                string startMenuFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Epic Games Account Switcher");
                Directory.CreateDirectory(startMenuFolder);
                string startMenuShortcut = Path.Combine(startMenuFolder, "Epic Games Account Switcher.lnk");
                Globals.CreateShortcut(targetExePath, startMenuShortcut, "Epic Games Account Switcher", "", targetExePath);

                string desktopShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Epic Games Account Switcher.lnk");
                Globals.CreateShortcut(targetExePath, desktopShortcut, "Epic Games Account Switcher", "", targetExePath);

                using (var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Epic-Games_Account_Switcher"))
                {
                    key.SetValue("DisplayName", "Epic Games Account Switcher");
                    key.SetValue("UninstallString", $"\"{targetExePath}\" --uninstall");
                    key.SetValue("DisplayIcon", targetExePath);
                    key.SetValue("Publisher", "Epic-Games_Account_Switcher Team");
                    key.SetValue("DisplayVersion", Globals.Version);
                    key.SetValue("InstallLocation", targetDir);
                }

                // Register protocol
                using (var key = Microsoft.Win32.Registry.ClassesRoot.CreateSubKey("epic-switcher"))
                {
                    key.SetValue("", "URL:epic-switcher Protocol");
                    key.SetValue("URL Protocol", "");
                    using (var shellKey = key.CreateSubKey(@"shell\open\command"))
                    {
                        shellKey.SetValue("", $"\"{targetExePath}\" \"%1\"");
                    }
                }

                MessageBox.Show("Installation Successful!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                
                Process.Start(new ProcessStartInfo { FileName = targetExePath, WorkingDirectory = targetDir, UseShellExecute = true });
                try { Mutex.ReleaseMutex(); } catch { }
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Installation failed: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                try { File.WriteAllText(Path.Combine(Globals.UserDataFolder, "InstallError.txt"), ex.ToString()); } catch { }
                Environment.Exit(1);
            }
        }

        private void PerformThoroughUninstall(bool confirmed)
        {
            if (!confirmed)
            {
                var res = MessageBox.Show("Are you sure you want to completely uninstall Epic Games Account Switcher and remove ALL saved data?\n\nThis will purge all accounts, settings, and shortcuts.", "Confirm Full Uninstall", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes) Environment.Exit(0);
            }

            if (!Globals.IsAdministrator)
            {
                try { Process.Start(new ProcessStartInfo { FileName = Environment.ProcessPath, Arguments = "--uninstall --confirmed", UseShellExecute = true, Verb = "runas" }); Environment.Exit(0); } catch { return; }
            }

            try
            {
                // 1. Identify paths before anything else
                string installPath = "";
                string regPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Epic-Games_Account_Switcher";
                
                using (var hklm64 = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
                using (var key = hklm64.OpenSubKey(regPath))
                {
                    installPath = key?.GetValue("InstallLocation")?.ToString() ?? "";
                }

                // 2. Aggressive Kill (excluding current process)
                var currentPid = Process.GetCurrentProcess().Id;
                string[] processesToKill = { "Epic-Games_Account_Switcher", "runas" };
                foreach (var name in processesToKill)
                {
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        if (p.Id == currentPid) continue;
                        try { p.Kill(true); p.WaitForExit(2000); } catch { }
                    }
                }

                // 3. Robust Registry Purge (All Views)
                string[] keysToDelete = {
                    regPath,
                    @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Epic-Games_Account_Switcher",
                    @"Software\Epic-Games_Account_Switcher"
                };

                var hives = new[] { Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryHive.CurrentUser };
                var views = new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 };

                foreach (var hive in hives)
                {
                    foreach (var view in views)
                    {
                        using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
                        foreach (var path in keysToDelete)
                        {
                            try { baseKey.DeleteSubKeyTree(path, false); } catch { }
                        }
                    }
                }
                try { Microsoft.Win32.Registry.ClassesRoot.DeleteSubKeyTree("epic-switcher", false); } catch { }

                // 4. Shortcut Removal
                string[] locations = {
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
                };

                foreach (var loc in locations)
                {
                    try {
                        string folder = Path.Combine(loc, "Epic Games Account Switcher");
                        if (Directory.Exists(folder)) Globals.ForceDeleteDirectory(folder);
                        string link = Path.Combine(loc, "Epic Games Account Switcher.lnk");
                        if (File.Exists(link)) File.Delete(link);
                    } catch { }
                }

                // 5. Success Message (Critical for PCA and User Feedback)
                MessageBox.Show("Epic Games Account Switcher has been successfully uninstalled.\n\nRemaining temporary files will be cleaned up automatically.", "Uninstall Complete", MessageBoxButton.OK, MessageBoxImage.Information);

                // 6. Final File Wipe via Background Script
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var mainDataFolder = Path.Combine(localAppData, "Epic-Games_Account_Switcher");

                string scriptPath = Path.Combine(Path.GetTempPath(), "epic_final_cleanup.bat");
                string script = "@echo off\r\n" +
                               "title Epic Cleanup\r\n" +
                               ":wait_loop\r\n" +
                               "tasklist /FI \"PID eq " + currentPid + "\" 2>NUL | find /I \"" + currentPid + "\" >NUL\r\n" +
                               "if \"%ERRORLEVEL%\"==\"0\" (timeout /t 1 /nobreak > nul & goto wait_loop)\r\n";

                if (!string.IsNullOrEmpty(installPath)) script += $"rd /s /q \"{installPath}\" > nul 2>&1\r\n";
                script += $"rd /s /q \"{mainDataFolder}\" > nul 2>&1\r\n";
                script += "del \"%~f0\"";
                
                File.WriteAllText(scriptPath, script);
                Process.Start(new ProcessStartInfo { FileName = "cmd.exe", Arguments = $"/c \"{scriptPath}\"", CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
            }
            catch (Exception ex) { MessageBox.Show("Critical Uninstall Error: " + ex.Message); }
            finally
            {
                try { Mutex.ReleaseMutex(); } catch { }
                Environment.Exit(0);
            }
        }
    }
}
