using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.ComponentModel;
using Epic_Games_Account_Switcher_Globals;
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace Epic_Games_Account_Switcher_Client
{
    public partial class App
    {
        public static string StartPage = "";
        private static Mutex _appMutex;
        private const string AppMutexName = "{A240C23D-6F45-4E92-9979-11E6CE10A22C}";
        private const string InstallerMutexName = "Global\\EpicSwitcherSetupMutex";
        private const string MsiMutexName = "Global\\_MSIExecute";
        private const string RegPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Epic-Games_Account_Switcher";
        private static string CurrentProcessPath => Process.GetCurrentProcess().MainModule.FileName;
        private static ProgressWindow _progress;

        protected override void OnExit(ExitEventArgs e) { try { if (_appMutex != null) { _appMutex.ReleaseMutex(); _appMutex.Dispose(); } } catch { } }

        [STAThread]
        protected override void OnStartup(StartupEventArgs e)
        {
            string logPath = Path.Combine(Path.GetTempPath(), "EpicSwitcher_Boot.log");
            try { File.AppendAllText(logPath, "[" + DateTime.Now + "] ON_STARTUP. Args: " + string.Join(" ", e.Args) + " | Path: " + CurrentProcessPath + Environment.NewLine); } catch { }
            
            AppDomain.CurrentDomain.UnhandledException += (s, ev) => 
            { 
                string err = ev.ExceptionObject.ToString();
                try { File.AppendAllText(logPath, "[FATAL_CRASH] " + err + Environment.NewLine); } catch { }
                MessageBox.Show("CRITICAL ERROR:\n\n" + err, "Epic Games Account Switcher", MessageBoxButton.OK, MessageBoxImage.Error);
            };

            bool isUninstall = e.Args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));
            bool isInstall = e.Args.Any(a => a.Equals("--installing", StringComparison.OrdinalIgnoreCase)) || !Globals.InstalledToProgramFiles();

            if (isUninstall) 
            { 
                Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown; 
                string waitPid = e.Args.FirstOrDefault(a => int.TryParse(a, out _));
                PerformThoroughUninstall(e.Args.Contains("--confirmed"), waitPid); 
                return; 
            }

            if (isInstall) 
            { 
                Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown; 
                PerformSmartInstall(e.Args.Contains("--preserve")); 
                return; 
            }

            try { File.AppendAllText(logPath, "[" + DateTime.Now + "] Proceeding to normal launch." + Environment.NewLine); } catch { }
            Globals.CreateDataFolder(false);
            Directory.SetCurrentDirectory(Globals.UserDataFolder);
            IsRunningAlready();
            try { new MainWindow().Show(); } catch (Exception ex) { try { File.AppendAllText(logPath, "[LAUNCH_ERROR] " + ex.ToString() + Environment.NewLine); } catch { } Environment.Exit(1); }
        }

        public static void IsRunningAlready()
        {
            try 
            { 
                _appMutex = new Mutex(true, AppMutexName, out bool createdNew);
                if (!createdNew)
                {
                    if (NativeFuncs.BringToFront()) Environment.Exit(0);
                    else { new CustomMessageBox("Already Running", "An instance is already running.").ShowDialog(); Environment.Exit(1056); }
                }
            }
            catch (AbandonedMutexException) { }
        }

        private static bool IsMsiBusy()
        {
            try 
            { 
                using (var mutex = Mutex.OpenExisting(MsiMutexName)) return true; 
            }
            catch (WaitHandleCannotBeOpenedException) { }
            catch { }

            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\InProgress")) { if (key != null) return true; }
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Updates\UpdateExeVolatile")) { if (key != null) return true; }
            } catch { }
            return false;
        }

        private static void KillAllProcesses(Action<string> update)
        {
            string[] names = { "Epic-Games_Account_Switcher", "runas", "Epic_Uninstaller", "epic_sw_del", "SingleFilePacker" };
            var cur = Process.GetCurrentProcess();
            var dirs = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic-Games_Account_Switcher").ToLower(),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Epic-Games_Account_Switcher").ToLower()
            };
            
            for (int retry = 0; retry < 3; retry++)
            {
                bool found = false;
                foreach (var n in names)
                {
                    foreach (var p in Process.GetProcessesByName(n))
                    {
                        if (p.Id == cur.Id) continue;
                        found = true;
                        update?.Invoke("AUTO-CLEANUP: Terminating " + p.ProcessName + " (" + p.Id + ")...");
                        try 
                        { 
                            p.Kill(); 
                            if (!p.WaitForExit(3000))
                            {
                                Process.Start(new ProcessStartInfo { FileName = "taskkill", Arguments = "/F /T /PID " + p.Id, CreateNoWindow = true, UseShellExecute = false })?.WaitForExit(2000);
                            }
                        } catch { }
                    }
                }

                try
                {
                    foreach (var p in Process.GetProcesses())
                    {
                        if (p.Id == cur.Id) continue;
                        try 
                        { 
                            if (!string.IsNullOrEmpty(p.MainModule?.FileName)) 
                            {
                                string procPath = p.MainModule.FileName.ToLower();
                                if (dirs.Any(d => procPath.StartsWith(d))) 
                                {
                                    found = true;
                                    update?.Invoke("AUTO-CLEANUP: Terminating path-match: " + p.ProcessName + " (" + p.Id + ")");
                                    p.Kill(); 
                                    if (!p.WaitForExit(3000))
                                    {
                                        Process.Start(new ProcessStartInfo { FileName = "taskkill", Arguments = "/F /T /PID " + p.Id, CreateNoWindow = true, UseShellExecute = false })?.WaitForExit(2000);
                                    }
                                }
                            }
                        } catch { }
                    }
                } catch { }

                if (!found) break;
                Thread.Sleep(800);
            }
        }

        private static void SafeDeleteDirectory(string path, Action<string> log)
        {
            if (!Directory.Exists(path)) return;
            log?.Invoke("CLEANUP: Purging directory: " + Path.GetFileName(path));
            
            bool success = false;
            for (int i = 0; i < 5; i++)
            {
                try { Directory.Delete(path, true); success = true; break; }
                catch (Exception)
                {
                    log?.Invoke("RETRY-CLEANUP: " + Path.GetFileName(path) + " is locked. Retrying... (" + (i + 1) + "/5)");
                    KillAllProcesses(log);
                    Thread.Sleep(1000);
                }
            }

            if (!success)
            {
                log?.Invoke("NOTICE: " + Path.GetFileName(path) + " is stubbornly locked. Attempting surgical overwrite...");
                try
                {
                    foreach (var f in Directory.GetFiles(path, "*", SearchOption.AllDirectories)) 
                    {
                        try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); } catch { }
                    }
                    Directory.Delete(path, true);
                }
                catch (Exception ex) { log?.Invoke("WARNING: Surgical purge failed: " + ex.Message); }
            }
        }

        private static void SleepWithTrace(int ms, string reason, Action<string> update = null)
        {
            update?.Invoke("SYSTEM: Waiting " + ms + "ms for " + reason + "...");
            Thread.Sleep(ms);
        }

        private void PerformSmartInstall(bool preserveRequested)
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var targetDir = Path.Combine(pf, "Epic-Games_Account_Switcher");
            var targetDir86 = Path.Combine(pf86, "Epic-Games_Account_Switcher");
            var targetExe = Path.Combine(targetDir, "Epic-Games_Account_Switcher.exe");
            var targetExe86 = Path.Combine(targetDir86, "Epic-Games_Account_Switcher.exe");

            if (!Globals.IsAdministrator)
            {
                bool foundByFile = File.Exists(targetExe) || File.Exists(targetExe86);
                string oldDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Epic-Games_Account_Switcher");
                bool foundByData = File.Exists(Path.Combine(Globals.UserDataFolder, "EG", "accounts.json")) || File.Exists(Path.Combine(oldDataPath, "LoginCache", "Epic_Games", "accounts.json"));
                bool foundByReg = false;
                try { using (var key = Registry.LocalMachine.OpenSubKey(RegPath)) if (key != null) foundByReg = true; } catch { }
                try { using (var key = Registry.CurrentUser.OpenSubKey(RegPath)) if (key != null) foundByReg = true; } catch { }
                
                bool preserve = false;
                if (foundByFile || foundByData || foundByReg)
                {
                    var result = new CustomMessageBox("Existing Installation Detected", 
                        "A previous installation of Epic Games Account Switcher has been detected on your system.\n\n" +
                        "To ensure a stable and clean environment, setup can perform a thorough cleanup of the existing version before proceeding.\n\n" +
                        "Would you like to preserve your existing account profiles and settings during this upgrade?", 
                        "Preserve & Upgrade", "Perform Clean Install").ShowDialog();
                    
                    if (result == true) preserve = true;
                    else if (result == false) preserve = false;
                    else Environment.Exit(0);
                }

                string args = "--installing" + (preserve ? " --preserve" : "");
                try { Process.Start(new ProcessStartInfo { FileName = CurrentProcessPath, Arguments = args, UseShellExecute = true, Verb = "runas" }); Environment.Exit(0); }
                catch { Environment.Exit(1); }
                return;
            }

            using (Mutex setupMutex = new Mutex(false, InstallerMutexName))
            {
                bool owned = false;
                try { owned = setupMutex.WaitOne(TimeSpan.FromSeconds(5), false); } catch (AbandonedMutexException) { owned = true; }
                
                if (!owned) { new CustomMessageBox("Installer Busy", "Another installation is already in progress.").ShowDialog(); Environment.Exit(0); return; }
                if (IsMsiBusy()) { new CustomMessageBox("System Busy", "Windows Installer is busy or a reboot is pending. Please wait and try again.").ShowDialog(); Environment.Exit(0); return; }

                bool isUpgrade = preserveRequested;
                string upgradeBackupPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Epic_Setup_Backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".zip");
                _progress = new ProgressWindow("Epic Games Account Switcher - Setup");
                string setupLog = Path.Combine(Path.GetTempPath(), "Epic_Setup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
                File.WriteAllText(setupLog, "--- Setup Initialized at " + DateTime.Now + " ---\n");

                _progress.Loaded += (s, e) =>
                {
                    Task.Run(() =>
                    {
                        Action<string> log = (st) => { try { File.AppendAllText(setupLog, "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + st + Environment.NewLine); _progress.UpdateStatus(st); } catch { } };
                        try
                        {
                            log("DEBUG: Installer Task Sequence Started.");
                            _progress.UpdateProgress(5);

                            if (isUpgrade)
                            {
                                log("UPGRADE: Starting data preservation sequence...");
                                try { AccountService.BackupAccounts(upgradeBackupPath, st => log("UPGRADE-IO: " + st)); log("UPGRADE: Data successfully preserved."); }
                                catch (Exception ex) { log("WARNING: Preservation failed: " + ex.Message); isUpgrade = false; }
                            }

                            log("SYSTEM AUDIT: Terminating active application instances...");
                            KillAllProcesses(st => log(st));
                            try { Process.Start(new ProcessStartInfo { FileName = "schtasks", Arguments = "/Delete /TN \"Epic-Games_Account_Switcher\" /F", CreateNoWindow = true, UseShellExecute = false })?.WaitForExit(2000); } catch { }
                            SleepWithTrace(1000, "InstallPostKill", log);
                            _progress.UpdateProgress(15);

                            log("REGISTRY: Wiping all configuration and metadata keys...");
                            string[] hives = { "HKLM", "HKCU" }; string[] views = { "64", "32" };
                            foreach (var hv in hives) foreach (var vw in views) 
                            { 
                                try 
                                { 
                                    var baseKey = vw == "64" ? RegistryKey.OpenBaseKey(hv == "HKLM" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64) : RegistryKey.OpenBaseKey(hv == "HKLM" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry32); 
                                    baseKey.DeleteSubKeyTree(RegPath, false); 
                                    baseKey.DeleteSubKeyTree(@"Software\Epic-Games_Account_Switcher", false);
                                } catch { } 
                            }
                            _progress.UpdateProgress(25);

                            log("FILESYSTEM: Purging remnants from target locations...");
                            SafeDeleteDirectory(targetDir, log);
                            SafeDeleteDirectory(targetDir86, log);
                            SafeDeleteDirectory(Path.Combine(Path.GetTempPath(), "Epic-Games_Account_Switcher"), log);
                            SafeDeleteDirectory(Path.Combine(Path.GetTempPath(), "Epic_Uninstaller_Workspace"), log);
                            SafeDeleteDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Epic-Games_Account_Switcher"), log);
                            if (!isUpgrade) SafeDeleteDirectory(Globals.UserDataFolder, log);
                            _progress.UpdateProgress(40);

                            log("FILESYSTEM: Initializing target directory -> " + targetDir);
                            Directory.CreateDirectory(targetDir);
                            
                            log("DEPLOYMENT: Deploying main executable...");
                            File.Copy(CurrentProcessPath, targetExe, true);
                            
                            log("DEPLOYMENT: Synchronizing resource payload...");
                            Globals.CopyFilesRecursive(AppDomain.CurrentDomain.BaseDirectory, targetDir, true);
                            _progress.UpdateProgress(70);

                            if (isUpgrade && File.Exists(upgradeBackupPath))
                            {
                                log("UPGRADE: Restoring account profiles from temporary backup...");
                                try 
                                { 
                                    AccountService.RestoreAccounts(upgradeBackupPath); 
                                    log("UPGRADE: Data restoration successful."); 
                                    log("SUCCESS: ACCOUNT PROFILES RESTORED.");
                                }
                                catch (Exception ex) 
                                { 
                                    log("ERROR: DATA RESTORATION FAILURE: " + ex.Message); 
                                    Application.Current.Dispatcher.Invoke(() => {
                                        new CustomMessageBox("Restore Failed", "Setup finished but your account profiles could not be automatically restored.\n\nYou can try manual restore from: " + upgradeBackupPath + "\n\nError: " + ex.Message, MessageBoxVisualStyle.Error).ShowDialog();
                                    });
                                }
                                SleepWithTrace(1000, "PostRestoreCooldown", log);
                            }

                            log("REGISTRY: Writing system uninstaller metadata...");
                            using (var key = Registry.LocalMachine.CreateSubKey(RegPath))
                            {
                                key.SetValue("DisplayName", "Epic Games Account Switcher");
                                key.SetValue("UninstallString", "\"" + targetExe + "\" --uninstall --confirmed");
                                key.SetValue("DisplayIcon", targetExe);
                                key.SetValue("DisplayVersion", Globals.Version);
                                key.SetValue("InstallLocation", targetDir);
                                key.SetValue("Publisher", "Epic Games Account Switcher Team");
                                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                            }
                            _progress.UpdateProgress(85);

                            log("SHELL: Registering system shortcuts...");
                            try
                            {
                                var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Epic Games Account Switcher.lnk");
                                var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Epic Games Account Switcher.lnk");
                                Globals.CreateShortcut(targetExe, startMenu, "Epic Games Account Switcher");
                                Globals.CreateShortcut(targetExe, desktop, "Epic Games Account Switcher");
                                log("SHELL: Shortcuts registered successfully.");
                            }
                            catch (Exception ex) { log("WARNING: Shortcut generation failed: " + ex.Message); }

                            _progress.UpdateProgress(100);
                            log("SUCCESS: Installation finished successfully.");
                            SleepWithTrace(1500, "InstallSuccessDisplay", log);
                            _progress.Dispatcher.Invoke(() => _progress.Close());
                            
                            Process.Start(new ProcessStartInfo { FileName = targetExe, WorkingDirectory = targetDir, UseShellExecute = true });
                            Environment.Exit(0);
                        }
                        catch (Exception ex)
                        {
                            try { File.AppendAllText(setupLog, "[FATAL] " + ex.ToString() + Environment.NewLine); } catch { }
                            _progress.Dispatcher.Invoke(() => {
                                try { new CustomMessageBox("Setup Failed", "A fatal error occurred during setup.\n\n" + ex.Message + "\n\nLog: " + setupLog, MessageBoxVisualStyle.Error).ShowDialog(); }
                                catch { MessageBox.Show("Setup Failed: " + ex.Message + "\n\nSee: " + setupLog, "Error", MessageBoxButton.OK, MessageBoxImage.Error); }
                                _progress.Close();
                            });
                            Environment.Exit(1);
                        }
                    });
                };
                _progress.ShowDialog();
            }
        }

        private void PerformThoroughUninstall(bool confirmed, string waitPid)
        {
            if (int.TryParse(waitPid, out int pid)) 
            {
                try { var parent = Process.GetProcessById(pid); parent.WaitForExit(10000); } catch { }
            }

            if (CurrentProcessPath.ToLower().Contains("program files"))
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "Epic_Uninstaller_Workspace");
                try 
                { 
                    if (Directory.Exists(tempDir)) SafeDeleteDirectory(tempDir, null);
                    Directory.CreateDirectory(tempDir);
                    Globals.CopyFilesRecursive(Path.GetDirectoryName(CurrentProcessPath), tempDir, true);
                } catch { }

                string tempExe = Path.Combine(tempDir, "Epic-Games_Account_Switcher.exe");
                Process.Start(new ProcessStartInfo { FileName = tempExe, Arguments = "--uninstall " + Process.GetCurrentProcess().Id + (confirmed ? " --confirmed" : ""), UseShellExecute = true, Verb = Globals.IsAdministrator ? "" : "runas" });
                Environment.Exit(0);
            }

            if (!Globals.IsAdministrator) 
            { 
                Process.Start(new ProcessStartInfo { FileName = CurrentProcessPath, Arguments = "--uninstall " + waitPid + (confirmed ? " --confirmed" : ""), UseShellExecute = true, Verb = "runas" }); 
                Environment.Exit(0); 
                return; 
            }

            if (!confirmed) 
            { 
                if (new CustomMessageBox("Confirm Uninstall", "Completely remove application and all data?", "Uninstall", "Cancel").ShowDialog() != true) Environment.Exit(0); 
            }

            using (Mutex setupMutex = new Mutex(false, InstallerMutexName))
            {
                bool owned = false;
                try { owned = setupMutex.WaitOne(TimeSpan.FromSeconds(5), false); } catch (AbandonedMutexException) { owned = true; }
                if (!owned) return;

                if (IsMsiBusy())
                {
                    MessageBox.Show("Windows Installer or a reboot is pending. Please wait and try again.", "System Busy", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _progress = new ProgressWindow("Epic Games Account Switcher - Uninstallation");
                _progress.Loaded += (s, e) =>
                {
                    Task.Run(() =>
                    {
                        Action<string> log = (st) => { _progress.UpdateStatus(st); };
                        try
                        {
                            _progress.UpdateProgress(5);
                            log("SYSTEM AUDIT: Terminating active application instances...");
                            KillAllProcesses(st => log(st));
                            try { Process.Start(new ProcessStartInfo { FileName = "schtasks", Arguments = "/Delete /TN \"Epic-Games_Account_Switcher\" /F", CreateNoWindow = true, UseShellExecute = false })?.WaitForExit(2000); } catch { }
                            SleepWithTrace(1000, "UninstallPostKill", log);
                            _progress.UpdateProgress(20);

                            log("REGISTRY: Wiping all configuration and metadata keys...");
                            try { Registry.LocalMachine.DeleteSubKeyTree(RegPath, false); } catch { }
                            string[] hives = { "HKLM", "HKCU" }; string[] views = { "64", "32" };
                            foreach (var hv in hives) foreach (var vw in views) 
                            { 
                                try 
                                { 
                                    var baseKey = vw == "64" ? RegistryKey.OpenBaseKey(hv == "HKLM" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64) : RegistryKey.OpenBaseKey(hv == "HKLM" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry32); 
                                    baseKey.DeleteSubKeyTree(RegPath, false); 
                                    baseKey.DeleteSubKeyTree(@"Software\Epic-Games_Account_Switcher", false);
                                } catch { } 
                            }
                            _progress.UpdateProgress(40);

                            log("SHELL: Purging system shortcuts...");
                            try { File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Epic Games Account Switcher.lnk")); } catch { }
                            try { File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Epic Games Account Switcher.lnk")); } catch { }
                            _progress.UpdateProgress(60);

                            log("FILESYSTEM: Purging application data and install directory...");
                            string targetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic-Games_Account_Switcher");
                            string targetDir86 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Epic-Games_Account_Switcher");
                            SafeDeleteDirectory(targetDir, log);
                            SafeDeleteDirectory(targetDir86, log);
                            SafeDeleteDirectory(Globals.UserDataFolder, log);
                            SafeDeleteDirectory(Path.Combine(Path.GetTempPath(), "Epic-Games_Account_Switcher"), log);
                            SafeDeleteDirectory(Path.Combine(Path.GetTempPath(), "Epic_Uninstaller_Workspace"), log);
                            _progress.UpdateProgress(90);

                            _progress.UpdateProgress(100);
                            log("SUCCESS: UNINSTALLATION COMPLETE!");
                            SleepWithTrace(2000, "UninstallSuccessDisplay", log);
                            _progress.Dispatcher.Invoke(() => _progress.Close());

                            string batch = Path.Combine(Path.GetTempPath(), "epic_sw_del.bat");
                            string tempDir = Path.GetDirectoryName(CurrentProcessPath);
                            bool isTempWorkspace = tempDir.ToLower().Contains("epic_uninstaller_workspace");

                            string batchContent = string.Format(
                                "@echo off\n" +
                                ":retry\n" +
                                "timeout /t 1 /nobreak > nul\n" +
                                "del /f /q \"{0}\"\n" +
                                "if exist \"{0}\" goto retry\n" +
                                (isTempWorkspace ? "rd /s /q \"" + tempDir + "\"\n" : "") +
                                "del \"%~f0\"", CurrentProcessPath);
                            File.WriteAllText(batch, batchContent);
                            Process.Start(new ProcessStartInfo { FileName = "cmd.exe", Arguments = "/c \"" + batch + "\"", CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
                            Environment.Exit(0);
                        }
                        catch (Exception ex) 
                        { 
                            log("FATAL ERROR: " + ex.Message); 
                            Thread.Sleep(5000); 
                            Environment.Exit(1); 
                        }
                    });
                };
                _progress.ShowDialog();
            }
        }
    }
}
