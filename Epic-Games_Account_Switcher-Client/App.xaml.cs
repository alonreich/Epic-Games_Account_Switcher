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
            Globals.WriteToLog($"=== Application Exiting (Exit Code: {e.ApplicationExitCode}) ===");
            try { Mutex.ReleaseMutex(); } catch { }
        }

        [STAThread]
        protected override void OnStartup(StartupEventArgs e)
        {
            Globals.WriteToLog("=== Application Starting ===");
            Globals.WriteToLog($"Version: {Globals.Version}");
            Globals.WriteToLog($"Arguments: {string.Join(" ", e.Args)}");

            AppDomain.CurrentDomain.UnhandledException += (s, ev) => {
                Globals.WriteToLog("[FATAL] Unhandled Exception occurred.", ev.ExceptionObject as Exception);
                try { File.WriteAllText(Path.Combine(Globals.UserDataFolder, "Crash.txt"), ev.ExceptionObject.ToString()); } catch { }
            };

            
            Resources["HeaderbarBackground"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#14151E"));
            Resources["HeaderbarForeground"] = System.Windows.Media.Brushes.White;
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

            
            bool mustInstall = !Globals.InstalledToProgramFiles();
            if (mustInstall) { PerformSmartInstall(); return; }

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
            try 
            { 
                if (!Mutex.WaitOne(TimeSpan.Zero, true)) 
                { 
                    if (NativeFuncs.BringToFront())
                    {
                        Environment.Exit(0);
                    }
                    else
                    {
                        MessageBox.Show("An \"Epic Games Account Switcher\" instance is already running but could not be focused. Please close it manually and try again.", "Already Running", MessageBoxButton.OK, MessageBoxImage.Warning);
                        Environment.Exit(1056);
                    }
                } 
            }
            catch (AbandonedMutexException) { }
        }

        private void PerformSmartInstall()
        {
            Globals.WriteToLog("[INSTALL] Starting Fluent Upgrade/Install flow.");
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var targetDir = Path.Combine(programFiles, "Epic-Games_Account_Switcher");
            var targetExePath = Path.Combine(targetDir, "Epic-Games_Account_Switcher.exe");
            string upgradeBackupZip = Path.Combine(Path.GetTempPath(), "Epic_Upgrade_Backup.zip");

            bool IsInstalledInAppWiz()
            {
                const string uninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Epic-Games_Account_Switcher";
                try
                {
                    using var hklm64 = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
                    using var key64 = hklm64.OpenSubKey(uninstallKey);
                    if (key64 != null) return true;
                }
                catch { }
                return false;
            }

            bool alreadyInstalled = Directory.Exists(targetDir) || File.Exists(targetExePath) || IsInstalledInAppWiz();
            Globals.WriteToLog($"[INSTALL] Already installed check: {alreadyInstalled}");

                                       
            if (!Environment.GetCommandLineArgs().Contains("--installing"))
            {
                if (alreadyInstalled)
                {
                    var choice = MessageBox.Show(
                        "A previous version of Epic Games Account Switcher is detected.\n\n" +
                        "The installer will now perform a Fluent Upgrade:\n" +
                        "1. Backup your current data\n" +
                        "2. Wipe old remnants\n" +
                        "3. Install the new version\n" +
                        "4. Restore your data automatically\n\n" +
                        "Proceed with Upgrade?",
                        "Upgrade Detected", MessageBoxButton.YesNo, MessageBoxImage.Question);

                    if (choice != MessageBoxResult.Yes) { Globals.WriteToLog("[INSTALL] User cancelled upgrade."); Environment.Exit(0); return; }
                }

                if (!Globals.IsAdministrator)
                {
                    Globals.WriteToLog("[INSTALL] Requesting admin elevation for install.");
                    try 
                    { 
                        Process.Start(new ProcessStartInfo { FileName = Environment.ProcessPath, Arguments = "--installing", UseShellExecute = true, Verb = "runas" }); 
                        Environment.Exit(0); 
                    }
                    catch (Exception ex) { Globals.WriteToLog("[INSTALL] Admin elevation failed.", ex); MessageBox.Show("Administrator approval is required to install/upgrade."); Environment.Exit(1); }
                    return;
                }
            }

            string currentStage = "Initializing";
            try
            {
                                  
                currentStage = "Stage 1: Backup Current Data";
                Globals.WriteToLog($"[INSTALL] {currentStage}");
                if (alreadyInstalled && Directory.Exists(Globals.UserDataFolder))
                {
                    AccountService.BackupAccounts(upgradeBackupZip);
                }

                                                   
                currentStage = "Stage 2: Thorough Uninstall & Wipe";
                Globals.WriteToLog($"[INSTALL] {currentStage}");
                Globals.KillProcess("Epic-Games_Account_Switcher", true);
                
                                        
                string regPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Epic-Games_Account_Switcher";
                using (var hklm64 = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
                {
                    hklm64.DeleteSubKeyTree(regPath, false);
                }
                Microsoft.Win32.Registry.ClassesRoot.DeleteSubKeyTree("epic-switcher", false);

                                     
                if (Directory.Exists(targetDir))
                {
                    try { Globals.ForceDeleteDirectory(targetDir); } catch { }
                }

                                                 
                if (Directory.Exists(Globals.UserDataFolder))
                {
                    foreach (var file in Directory.GetFiles(Globals.UserDataFolder, "*.txt"))
                    {
                        try { File.Delete(file); } catch { }
                    }
                }

                                   
                currentStage = "Stage 3: Installing New Version";
                Globals.WriteToLog($"[INSTALL] {currentStage}");
                Directory.CreateDirectory(targetDir);
                
                string sourceBase = AppDomain.CurrentDomain.BaseDirectory;
                Globals.WriteToLog($"[INSTALL] Copying from runtime base: {sourceBase}");

                // Deep copy all files from current base directory
                Globals.CopyFilesRecursive(sourceBase, targetDir, true);
                
                if (File.Exists(Environment.ProcessPath)) File.Copy(Environment.ProcessPath, targetExePath, true);

                // EXPLICIT FORCE COPY for OpenCV_Anchors
                var sourceAnchors = Path.Combine(sourceBase, "OpenCV_Anchors");
                var targetAnchors = Path.Combine(targetDir, "OpenCV_Anchors");
                
                Globals.WriteToLog($"[INSTALL] Explicit anchor check: {sourceAnchors}");
                if (Directory.Exists(sourceAnchors))
                {
                    Directory.CreateDirectory(targetAnchors);
                    var files = Directory.GetFiles(sourceAnchors);
                    foreach(var file in files)
                    {
                        var destFile = Path.Combine(targetAnchors, Path.GetFileName(file));
                        File.Copy(file, destFile, true);
                        Globals.WriteToLog($"[INSTALL] Copied anchor file: {Path.GetFileName(file)}");
                    }
                    Globals.WriteToLog($"[INSTALL] Force copied {files.Length} anchors to {targetAnchors}");
                }
                else
                {
                    Globals.WriteToLog("[INSTALL] ERROR: OpenCV_Anchors source folder NOT FOUND in extraction base.");
                    // Fallback: Check if it's one level up?
                    var fallbackSource = Path.Combine(Path.GetDirectoryName(sourceBase.TrimEnd(Path.DirectorySeparatorChar)) ?? "", "OpenCV_Anchors");
                    if (Directory.Exists(fallbackSource))
                    {
                         Globals.WriteToLog($"[INSTALL] Found fallback anchors at: {fallbackSource}");
                         // ... copy from here
                    }
                }

                            
                string commonPrograms = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
                string startMenuFolder = Path.Combine(commonPrograms, "Epic Games Account Switcher");
                Directory.CreateDirectory(startMenuFolder);
                Globals.CreateShortcut(targetExePath, Path.Combine(startMenuFolder, "Epic Games Account Switcher.lnk"), "Epic Games Account Switcher", "", targetExePath);
                Globals.CreateShortcut(targetExePath, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Epic Games Account Switcher.lnk"), "Epic Games Account Switcher", "", targetExePath);

                                  
                using (var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(regPath))
                {
                    key.SetValue("DisplayName", "Epic Games Account Switcher");
                    key.SetValue("UninstallString", $"\"{targetExePath}\" --uninstall");
                    key.SetValue("DisplayIcon", targetExePath);
                    key.SetValue("DisplayVersion", Globals.Version);
                    key.SetValue("InstallLocation", targetDir);
                }

                using (var key = Microsoft.Win32.Registry.ClassesRoot.CreateSubKey("epic-switcher"))
                {
                    key.SetValue("", "URL:epic-switcher Protocol");
                    key.SetValue("URL Protocol", "");
                    using var commandKey = key.CreateSubKey(@"shell\open\command");
                    commandKey.SetValue("", $"\"{targetExePath}\" \"%1\"");
                }

                                   
                currentStage = "Stage 4: Restoring Data";
                Globals.WriteToLog($"[INSTALL] {currentStage}");
                if (File.Exists(upgradeBackupZip))
                {
                    AccountService.RestoreAccounts(upgradeBackupZip);
                    try { File.Delete(upgradeBackupZip); } catch { }
                }

                Globals.WriteToLog("[INSTALL] Success!");
                MessageBox.Show("Upgrade and Installation Successful!", "Fluent Upgrade Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                Process.Start(new ProcessStartInfo { FileName = targetExePath, WorkingDirectory = targetDir, UseShellExecute = true });
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Globals.WriteToLog($"[INSTALL] FAILED at {currentStage}", ex);
                string errorMsg = $"[UPGRADE ERROR] {currentStage} failed.\n\nDetails: {ex.Message}";
                MessageBox.Show(errorMsg, "Upgrade Error", MessageBoxButton.OK, MessageBoxImage.Error);
                
                try 
                { 
                    Directory.CreateDirectory(Globals.UserDataFolder);
                    File.WriteAllText(Path.Combine(Globals.UserDataFolder, "UpgradeError.txt"), $"{DateTime.Now}: {errorMsg}\n\nFull Stack Trace:\n{ex}"); 
                } catch { }
                
                Environment.Exit(1);
            }
        }

        private void PerformThoroughUninstall(bool confirmed)
        {
            Globals.WriteToLog("[UNINSTALL] Starting Uninstall flow.");
            if (!confirmed)
            {
                var res = MessageBox.Show("Are you sure you want to completely uninstall Epic Games Account Switcher and remove ALL saved data?\n\nThis will purge all accounts, settings, and shortcuts.", "Confirm Full Uninstall", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes) { Globals.WriteToLog("[UNINSTALL] User cancelled."); Environment.Exit(0); }
            }

            if (!Globals.IsAdministrator)
            {
                Globals.WriteToLog("[UNINSTALL] Requesting admin elevation.");
                try { Process.Start(new ProcessStartInfo { FileName = Environment.ProcessPath, Arguments = "--uninstall --confirmed", UseShellExecute = true, Verb = "runas" }); Environment.Exit(0); } catch (Exception ex) { Globals.WriteToLog("[UNINSTALL] Elevation failed.", ex); return; }
            }

            try
            {
                
                try 
                {
                    Globals.WriteToLog("[UNINSTALL] Creating final backup in Downloads.");
                    string downloadsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                    string backupFile = Path.Combine(downloadsFolder, "Epic-Games_Account_Switcher_Backup.zip");
                    AccountService.BackupAccounts(backupFile);
                }
                catch { }

                
                string installPath = "";
                string regPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Epic-Games_Account_Switcher";
                
                using (var hklm64 = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
                using (var key = hklm64.OpenSubKey(regPath))
                {
                    installPath = key?.GetValue("InstallLocation")?.ToString() ?? "";
                }

                
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

                
                Globals.WriteToLog("[UNINSTALL] Cleaning registry.");
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

                
                Globals.WriteToLog("[UNINSTALL] Cleaning shortcuts.");
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

                
                MessageBox.Show("Epic Games Account Switcher has been successfully uninstalled.\n\nRemaining temporary files will be cleaned up automatically.", "Uninstall Complete", MessageBoxButton.OK, MessageBoxImage.Information);

                
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
                Globals.WriteToLog("[UNINSTALL] Success! Cleanup script created.");
                Process.Start(new ProcessStartInfo { FileName = "cmd.exe", Arguments = $"/c \"{scriptPath}\"", CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
            }
            catch (Exception ex) { Globals.WriteToLog("[UNINSTALL] FAILED", ex); MessageBox.Show("Critical Uninstall Error: " + ex.Message); }
            finally
            {
                try { Mutex.ReleaseMutex(); } catch { }
                Environment.Exit(0);
            }
        }
    }
}
