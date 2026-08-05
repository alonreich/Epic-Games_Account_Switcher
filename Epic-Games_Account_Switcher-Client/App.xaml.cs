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
        private static string _extractDir;

        protected override void OnExit(ExitEventArgs e)
        {
            try { Globals.WriteToLog("APP_SHUTDOWN ExitCode=" + e.ApplicationExitCode); } catch { }
            try { if (_appMutex != null) { _appMutex.ReleaseMutex(); _appMutex.Dispose(); } } catch { }
        }

        [STAThread]
        protected override void OnStartup(StartupEventArgs e)
        {
            var logDirOverride = GetArgValue(e.Args, "--log-dir");
            try { if (!string.IsNullOrWhiteSpace(logDirOverride)) Directory.CreateDirectory(Path.GetFullPath(logDirOverride)); } catch { }

            var userDataOverride = GetArgValue(e.Args, "--user-data");
            if (!string.IsNullOrWhiteSpace(userDataOverride)) Globals.UserDataFolderOverride = userDataOverride;
            if (!string.IsNullOrWhiteSpace(logDirOverride)) Globals.LogFolderOverride = logDirOverride;
            _extractDir = GetArgValue(e.Args, "--extract-dir");
            bool isDevMode = e.Args.Any(a => a.Equals("--dev", StringComparison.OrdinalIgnoreCase));
            Globals.WriteToLog("APP_START Mode=" + (isDevMode ? "Dev" : "Normal") + "; ArgsCount=" + (e.Args == null ? 0 : e.Args.Length) + "; Process=" + Path.GetFileName(CurrentProcessPath));
            
            AppDomain.CurrentDomain.UnhandledException += (s, ev) => 
            { 
                string err = ev.ExceptionObject.ToString();
                Globals.WriteToLog("[FATAL_CRASH] " + err);
                MessageBox.Show("CRITICAL ERROR:\n\n" + err, "Epic Games Account Switcher", MessageBoxButton.OK, MessageBoxImage.Error);
            };

            bool isUninstall = e.Args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));
            bool isInstall = !isDevMode && (e.Args.Any(a => a.Equals("--installing", StringComparison.OrdinalIgnoreCase)) || !Globals.InstalledToProgramFiles());

            if (isUninstall) 
            { 
                Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown; 
                string waitPid = e.Args.FirstOrDefault(a => int.TryParse(a, out _));
                PerformThoroughUninstall(e.Args.Contains("--confirmed"), waitPid, _extractDir);
                return; 
            }

            if (isInstall) 
            { 
                Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown; 
                PerformSmartInstall(e.Args.Contains("--preserve"), userDataOverride, GetArgValue(e.Args, "--upgrade-backup"), _extractDir);
                return; 
            }

            Globals.WriteToLog("APP_LAUNCH_CONTINUE Mode=" + (isDevMode ? "Dev" : "Normal"));
            Globals.CreateDataFolder(false);
            Directory.SetCurrentDirectory(Globals.UserDataFolder);
            AccountService.CleanOrphanedTempFolders();
            IsRunningAlready(isDevMode);
            if (AppSettings.Instance.OpenEpicLauncherOnStartup)
            {
                AccountService.PrepareLauncherForInitialSync();
            }
            try { new MainWindow().Show(); } catch (Exception ex) { Globals.WriteToLog("[LAUNCH_ERROR]", ex); if (_appMutex != null) { try { _appMutex.Dispose(); } catch { } _appMutex = null; } Environment.Exit(1); }
        }

        public static void IsRunningAlready(bool devMode = false)
        {
            try 
            { 
                var mutexName = devMode ? AppMutexName + ".DEV" : AppMutexName;
                _appMutex = new Mutex(true, mutexName, out bool createdNew);
                if (!createdNew)
                {
                    if (_appMutex != null) { try { _appMutex.Dispose(); } catch { } _appMutex = null; }
                    if (!devMode && NativeFuncs.BringToFront()) Environment.Exit(0);
                    else { new CustomMessageBox(devMode ? "Dev Instance Running" : "Already Running", devMode ? "A development instance is already running." : "An instance is already running.").ShowDialog(); Environment.Exit(1056); }
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

        private static string GetArgValue(string[] args, string name)
        {
            if (args == null || string.IsNullOrWhiteSpace(name)) return null;
            for (var i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) continue;
                return i + 1 < args.Length ? args[i + 1] : null;
            }
            return null;
        }

        private static string QuoteArg(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
        }

        private static string BuildInstallArguments(bool preserve, string userDataPath, string upgradeBackupPath, string extractDir)
        {
            var args = new List<string> { "--installing" };
            if (preserve) args.Add("--preserve");
            if (!string.IsNullOrWhiteSpace(userDataPath)) { args.Add("--user-data"); args.Add(QuoteArg(userDataPath)); }
            if (!string.IsNullOrWhiteSpace(upgradeBackupPath)) { args.Add("--upgrade-backup"); args.Add(QuoteArg(upgradeBackupPath)); }
            if (!string.IsNullOrWhiteSpace(extractDir)) { args.Add("--extract-dir"); args.Add(QuoteArg(extractDir)); }
            return string.Join(" ", args);
        }

        private static string BuildUninstallArguments(string waitPid, bool confirmed, string userDataPath, string extractDir)
        {
            var args = new List<string> { "--uninstall" };
            if (!string.IsNullOrWhiteSpace(waitPid)) args.Add(waitPid);
            if (confirmed) args.Add("--confirmed");
            if (!string.IsNullOrWhiteSpace(userDataPath)) { args.Add("--user-data"); args.Add(QuoteArg(userDataPath)); }
            if (!string.IsNullOrWhiteSpace(extractDir)) { args.Add("--extract-dir"); args.Add(QuoteArg(extractDir)); }
            return string.Join(" ", args);
        }

        private static bool IsPathUnder(string path, string root)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return false;
                var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static bool IsOwnedProcessPath(string processPath, IEnumerable<string> ownedRoots)
        {
            if (string.IsNullOrWhiteSpace(processPath) || ownedRoots == null) return false;
            return ownedRoots.Any(root => IsPathUnder(processPath, root) || string.Equals(Path.GetFullPath(processPath), Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsSafeTempExtractDir(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return false;
                var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) &&
                       Path.GetFileName(full).StartsWith("EpicSwitcherInstaller_", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static void ScheduleDirectoryDeleteAfterExit(string path, Action<string> log = null)
        {
            if (!IsSafeTempExtractDir(path)) return;
            try
            {
                string batchPath = Path.Combine(Path.GetTempPath(), "del_payload_" + Guid.NewGuid().ToString("N") + ".bat");
                string batchContent = string.Format("@echo off\r\ntimeout /t 2 /nobreak > nul\r\nrd /s /q \"{0}\"\r\ndel \"%~f0\"\r\n", path);
                File.WriteAllText(batchPath, batchContent);
                Process.Start(new ProcessStartInfo { 
                    FileName = "cmd.exe", 
                    Arguments = "/c \"" + batchPath + "\"", 
                    CreateNoWindow = true, 
                    WindowStyle = ProcessWindowStyle.Hidden 
                });
                log?.Invoke("CLEANUP: Scheduled extracted payload cleanup.");
            }
            catch (Exception ex) { log?.Invoke("WARNING: Could not schedule extracted payload cleanup: " + ex.Message); }
        }

        private static void PurgeOldExtractedPayloads(Action<string> log = null)
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(Path.GetTempPath(), "EpicSwitcherInstaller_*", SearchOption.TopDirectoryOnly))
                {
                    if (string.Equals(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(_extractDir ?? "").TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        if (Directory.GetCreationTimeUtc(dir) < DateTime.UtcNow.AddHours(-12)) SafeDeleteDirectory(dir, log);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static void KillAllProcesses(Action<string> update)
        {
            string[] names = { "Epic-Games_Account_Switcher", "Epic_Uninstaller", "epic_sw_del", "SingleFilePacker" };
            var cur = Process.GetCurrentProcess();
            var dirs = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic-Games_Account_Switcher").ToLower(),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Epic-Games_Account_Switcher").ToLower(),
                Path.Combine(Path.GetTempPath(), "Epic_Uninstaller_Workspace").ToLower()
            };
            try
            {
                var currentDir = Path.GetDirectoryName(CurrentProcessPath);
                if (!string.IsNullOrWhiteSpace(currentDir)) dirs.Add(currentDir.ToLower());
                if (!string.IsNullOrWhiteSpace(_extractDir)) dirs.Add(_extractDir.ToLower());
            }
            catch { }
            
            for (int retry = 0; retry < 3; retry++)
            {
                bool found = false;
                foreach (var n in names)
                {
                    var procs = Process.GetProcessesByName(n);
                    foreach (var p in procs)
                    {
                        using (p)
                        {
                            if (p.Id == cur.Id) continue;
                            try 
                            { 
                                var path = "";
                                try { if (p.MainModule != null) path = p.MainModule.FileName; } catch { }
                                if (!IsOwnedProcessPath(path, dirs)) continue;
                                found = true;
                                update?.Invoke("AUTO-CLEANUP: Terminating owned process " + p.ProcessName + " (" + p.Id + ")...");
                                p.Kill(); 
                                if (!p.WaitForExit(3000))
                                {
                                    Process.Start(new ProcessStartInfo { FileName = "taskkill", Arguments = "/F /T /PID " + p.Id, CreateNoWindow = true, UseShellExecute = false })?.WaitForExit(2000);
                                }
                            } catch { }
                        }
                    }
                }

                try
                {
                    var allProcs = Process.GetProcesses();
                    foreach (var p in allProcs)
                    {
                        using (p)
                        {
                            if (p.Id == cur.Id) continue;
                            try 
                            { 
                                if (!string.IsNullOrEmpty(p.MainModule?.FileName)) 
                                {
                                    string procPath = p.MainModule.FileName;
                                    if (IsOwnedProcessPath(procPath, dirs))
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

        private static bool TryPreserveLegacyAccountData(string oldDataPath, Action<string> log)
        {
            try
            {
                var legacyRoot = Path.Combine(oldDataPath, "LoginCache", "Epic_Games");
                var legacyAccounts = Path.Combine(legacyRoot, "accounts.json");
                if (!File.Exists(legacyAccounts)) return true;

                var newRoot = Path.Combine(Globals.UserDataFolder, "EG");
                var newAccounts = Path.Combine(newRoot, "accounts.json");
                if (!File.Exists(newAccounts))
                {
                    Directory.CreateDirectory(newRoot);
                    if (!Globals.CopyFilesRecursive(legacyRoot, newRoot, true))
                    {
                        log?.Invoke("WARNING: Legacy account import failed. Existing legacy data will be left untouched.");
                        return false;
                    }

                    log?.Invoke("UPGRADE: Legacy account data imported into the current profile store.");
                    return true;
                }

                var preservedRoot = Path.Combine(Globals.UserDataFolder, "Legacy_Import_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                Directory.CreateDirectory(preservedRoot);
                if (!Globals.CopyFilesRecursive(legacyRoot, preservedRoot, true))
                {
                    log?.Invoke("WARNING: Legacy account preservation copy failed. Existing legacy data will be left untouched.");
                    return false;
                }

                log?.Invoke("UPGRADE: Existing new-format data found. Legacy account data was preserved at: " + preservedRoot);
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke("WARNING: Legacy account preservation failed: " + ex.Message);
                return false;
            }
        }

        private static bool HasLegacyAccountData(string oldDataPath)
        {
            try { return File.Exists(Path.Combine(oldDataPath, "LoginCache", "Epic_Games", "accounts.json")); }
            catch { return false; }
        }

        private static void SleepWithTrace(int ms, string reason, Action<string> update = null)
        {
            update?.Invoke("SYSTEM: Waiting " + ms + "ms for " + reason + "...");
            Thread.Sleep(ms);
        }

        private void PerformSmartInstall(bool preserveRequested, string sourceUserDataPath, string upgradeBackupOverride, string extractDir)
        {
            var statusText = new System.Windows.Controls.TextBlock
            {
                Text = "Initializing Setup Environment...",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = System.Windows.Media.Brushes.LightGray,
                Margin = new Thickness(10)
            };
            
            var scrollViewer = new System.Windows.Controls.ScrollViewer
            {
                Content = statusText,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                Margin = new Thickness(5, 0, 5, 5)
            };

            var grid = new System.Windows.Controls.Grid();
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            
            var titleText = new System.Windows.Controls.TextBlock
            {
                Text = "EPIC GAMES ACCOUNT SWITCHER SETUP",
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = System.Windows.Media.Brushes.White,
                Margin = new Thickness(10)
            };
            
            System.Windows.Controls.Grid.SetRow(titleText, 0);
            System.Windows.Controls.Grid.SetRow(scrollViewer, 1);
            grid.Children.Add(titleText);
            grid.Children.Add(scrollViewer);

            Window setupSplash = new Window
            {
                Title = "Epic Games Account Switcher - Setup",
                Width = 500,
                Height = 250,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                WindowStyle = WindowStyle.None,
                Topmost = true,
                ShowInTaskbar = true,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(31, 35, 53)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(122, 162, 247)),
                BorderThickness = new Thickness(1),
                Content = grid
            };
            Action<string> splashLog = (msg) => 
            {
                Application.Current.Dispatcher.Invoke(() => 
                {
                    statusText.Text += "\n[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg;
                    scrollViewer.ScrollToEnd();
                }, System.Windows.Threading.DispatcherPriority.Render);
            };
            
            bool isUpgrade = preserveRequested;
            bool proceedToInstall = false;
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var targetDir = Path.Combine(pf, "Epic-Games_Account_Switcher");
            var targetDir86 = Path.Combine(pf86, "Epic-Games_Account_Switcher");
            var targetExe = Path.Combine(targetDir, "Epic-Games_Account_Switcher.exe");
            var targetExe86 = Path.Combine(targetDir86, "Epic-Games_Account_Switcher.exe");
            string oldDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Epic-Games_Account_Switcher");
            string upgradeBackupPath = !string.IsNullOrWhiteSpace(upgradeBackupOverride) ? upgradeBackupOverride : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Epic_Setup_Backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".egbackup");

            setupSplash.Loaded += (s, e) =>
            {
                Task.Run(() =>
                {
                    splashLog("Starting pre-installation tasks...");
                    KillAllProcesses(splashLog);

                    bool foundByFile = File.Exists(targetExe) || File.Exists(targetExe86);
                    bool foundByData = File.Exists(Path.Combine(Globals.UserDataFolder, "EG", "accounts.json")) || File.Exists(Path.Combine(oldDataPath, "LoginCache", "Epic_Games", "accounts.json"));
                    bool foundByReg = false;
                    try { using (var key = Registry.LocalMachine.OpenSubKey(RegPath)) if (key != null) foundByReg = true; } catch { }
                    try { using (var key = Registry.CurrentUser.OpenSubKey(RegPath)) if (key != null) foundByReg = true; } catch { }

                    if (!Globals.IsAdministrator)
                    {
                        bool preserve = false;
                        if (foundByFile || foundByData || foundByReg)
                        {
                            try { Directory.CreateDirectory(Path.GetDirectoryName(upgradeBackupPath)); AccountService.BackupAccounts(upgradeBackupPath, splashLog); } catch { }
                            Application.Current.Dispatcher.Invoke(() => 
                            {
                                setupSplash.Hide();
                                var msgBox = new CustomMessageBox("Existing Installation Detected", 
                                    "A previous installation of Epic Games Account Switcher has been detected on your system. A safety backup of your profiles has been saved to your Downloads folder automatically.\n\n" +
                                    "To ensure a stable and clean environment, setup can perform a thorough cleanup of the existing version before proceeding.\n\n" +
                                    "Would you like to preserve your existing account profiles and settings during this upgrade?", 
                                    "Preserve & Upgrade", "Perform Clean Install", "Cancel Setup");
                                msgBox.ShowDialog();
                                var result = msgBox.Result;
                                if (result == CustomDialogResult.Primary) preserve = true; else if (result == CustomDialogResult.Secondary) preserve = false; else Environment.Exit(0);
                            });
                        }
                        string args = BuildInstallArguments(preserve, Globals.UserDataFolder, upgradeBackupPath, extractDir);
                        try { Process.Start(new ProcessStartInfo { FileName = CurrentProcessPath, Arguments = args, UseShellExecute = true, Verb = "runas" }); Environment.Exit(0); } catch { Environment.Exit(1); }
                        return;
                    }

                    if (!preserveRequested && (foundByFile || foundByData || foundByReg))
                    {
                        try { Directory.CreateDirectory(Path.GetDirectoryName(upgradeBackupPath)); AccountService.BackupAccounts(upgradeBackupPath, splashLog); } catch { }
                        Application.Current.Dispatcher.Invoke(() => 
                        {
                            setupSplash.Hide();
                            var msgBox = new CustomMessageBox("Existing Installation Detected", 
                                    "A previous installation of Epic Games Account Switcher has been detected on your system. A safety backup of your profiles has been saved to your Downloads folder automatically.\n\n" +
                                    "To ensure a stable and clean environment, setup can perform a thorough cleanup of the existing version before proceeding.\n\n" +
                                    "Would you like to preserve your existing account profiles and settings during this upgrade?", 
                                    "Preserve & Upgrade", "Perform Clean Install", "Cancel Setup");
                            msgBox.ShowDialog();
                            var result = msgBox.Result;
                            if (result == CustomDialogResult.Primary) isUpgrade = true; else if (result == CustomDialogResult.Secondary) isUpgrade = false; else Environment.Exit(0);
                        });
                    }

                    proceedToInstall = true;
                    Application.Current.Dispatcher.Invoke(() => setupSplash.Close());
                });
            };

            setupSplash.ShowDialog();
            if (!proceedToInstall) return;

            if (!string.IsNullOrWhiteSpace(sourceUserDataPath)) Globals.UserDataFolderOverride = sourceUserDataPath;

            using (Mutex setupMutex = new Mutex(false, InstallerMutexName))
            {
                bool owned = false;
                try { owned = setupMutex.WaitOne(TimeSpan.FromSeconds(5), false); } catch (AbandonedMutexException) { owned = true; }
                
                if (!owned) { if (setupSplash != null) { setupSplash.Close(); setupSplash = null; } new CustomMessageBox("Installer Busy", "Another installation is already in progress.").ShowDialog(); Environment.Exit(0); return; }
                if (IsMsiBusy()) { if (setupSplash != null) { setupSplash.Close(); setupSplash = null; } new CustomMessageBox("System Busy", "Windows Installer is busy or a reboot is pending. Please wait and try again.").ShowDialog(); Environment.Exit(0); return; }
                if (setupSplash != null) { setupSplash.Close(); setupSplash = null; }
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

                            PurgeOldExtractedPayloads(log);
                            log("SYSTEM AUDIT: Terminating active application instances...");
                            KillAllProcesses(st => log(st));
                            try { Process.Start(new ProcessStartInfo { FileName = "schtasks", Arguments = "/Delete /TN \"Epic-Games_Account_Switcher\" /F", CreateNoWindow = true, UseShellExecute = false })?.WaitForExit(2000); } catch { }
                            SleepWithTrace(1000, "InstallPostKill", log);
                            _progress.UpdateProgress(15);

                            var legacyDataPreserved = !HasLegacyAccountData(oldDataPath);
                            if (isUpgrade && !legacyDataPreserved)
                            {
                                log("UPGRADE: Legacy account data detected. Preserving before cleanup...");
                                legacyDataPreserved = TryPreserveLegacyAccountData(oldDataPath, log);
                            }

                            if (isUpgrade)
                            {
                                log("UPGRADE: Starting data preservation sequence...");
                                try { Directory.CreateDirectory(Path.GetDirectoryName(upgradeBackupPath)); } catch { }
                                try { AccountService.BackupAccounts(upgradeBackupPath, st => log("UPGRADE-IO: " + st)); log("UPGRADE: Data successfully preserved."); }
                                catch (Exception ex) 
                                { 
                                    log("FATAL: Preservation failed (" + ex.Message + "). Aborting upgrade to protect account profiles."); 
                                    throw new InvalidOperationException("Upgrade backup preservation failed: " + ex.Message, ex); 
                                }
                            }

                            log("REGISTRY: Wiping all configuration and metadata keys...");
                            string[] hives = { "HKLM", "HKCU" }; string[] views = { "64", "32" };
                            foreach (var hv in hives) foreach (var vw in views) 
                            { 
                                try 
                                { 
                                    using (var baseKey = vw == "64" ? RegistryKey.OpenBaseKey(hv == "HKLM" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64) : RegistryKey.OpenBaseKey(hv == "HKLM" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry32))
                                    {
                                        baseKey.DeleteSubKeyTree(RegPath, false); 
                                        baseKey.DeleteSubKeyTree(@"Software\Epic-Games_Account_Switcher", false);
                                    }
                                } catch { } 
                            }
                            _progress.UpdateProgress(25);

                            log("FILESYSTEM: Purging remnants from target locations...");
                            SafeDeleteDirectory(targetDir, log);
                            SafeDeleteDirectory(targetDir86, log);
                            SafeDeleteDirectory(Path.Combine(Path.GetTempPath(), "Epic-Games_Account_Switcher"), log);
                            SafeDeleteDirectory(Path.Combine(Path.GetTempPath(), "Epic_Uninstaller_Workspace"), log);
                            if (!isUpgrade || legacyDataPreserved || !HasLegacyAccountData(oldDataPath))
                            {
                                SafeDeleteDirectory(oldDataPath, log);
                            }
                            else
                            {
                                log("WARNING: Legacy data folder was not deleted because preservation did not complete: " + oldDataPath);
                            }
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
                                        var backupHint = !string.IsNullOrWhiteSpace(upgradeBackupPath) && File.Exists(upgradeBackupPath)
                                            ? upgradeBackupPath
                                            : "No valid backup path was available. Check setup log: " + setupLog;
                                        new CustomMessageBox("Restore Failed", "Setup finished but your account profiles could not be automatically restored.\n\nManual restore backup: " + backupHint + "\n\nSetup log: " + setupLog + "\n\nError: " + ex.Message, MessageBoxVisualStyle.Error).ShowDialog();
                                    });
                                }
                                SleepWithTrace(1000, "PostRestoreCooldown", log);
                            }

                            log("REGISTRY: Writing system uninstaller metadata...");
                            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                            using (var key = baseKey.CreateSubKey(RegPath))
                            {
                                key.SetValue("DisplayName", "Epic Games Account Switcher");
                                key.SetValue("UninstallString", "\"" + targetExe + "\" " + BuildUninstallArguments(null, true, Globals.UserDataFolder, null));
                                key.SetValue("DisplayIcon", targetExe);
                                key.SetValue("DisplayVersion", Globals.Version);
                                key.SetValue("InstallLocation", targetDir);
                                key.SetValue("Publisher", "Epic Games Account Switcher Team");
                                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                            }
                            _progress.UpdateProgress(85);

                            log("SHELL: Probing desktop and start menu directories for shortcuts...");
                            try
                            {
                                var shortcutName = "Epic Games Account Switcher.lnk";
                                foreach (var sm in Globals.GetProbedStartMenuDirectories())
                                {
                                    var path = Path.Combine(sm, shortcutName);
                                    Globals.CreateShortcut(targetExe, path, "Epic Games Account Switcher");
                                    log("SHELL: Created start menu shortcut -> " + path);
                                }
                                foreach (var dt in Globals.GetProbedDesktopDirectories())
                                {
                                    var path = Path.Combine(dt, shortcutName);
                                    Globals.CreateShortcut(targetExe, path, "Epic Games Account Switcher");
                                    log("SHELL: Created desktop shortcut -> " + path);
                                }
                                log("SHELL: Shortcuts registered successfully.");
                            }
                            catch (Exception ex) { log("WARNING: Shortcut generation failed: " + ex.Message); }

                            _progress.UpdateProgress(100);
                            log("SUCCESS: Installation finished successfully.");
                            SleepWithTrace(1500, "InstallSuccessDisplay", log);
                            _progress.Dispatcher.Invoke(() => _progress.Close());
                            ScheduleDirectoryDeleteAfterExit(extractDir, log);
                            
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
                            ScheduleDirectoryDeleteAfterExit(extractDir);
                            Environment.Exit(1);
                        }
                    });
                };
                _progress.ShowDialog();
            }
        }

        private void PerformThoroughUninstall(bool confirmed, string waitPid, string extractDir)
        {
            if (int.TryParse(waitPid, out int pid)) 
            {
                try { var parent = Process.GetProcessById(pid); parent.WaitForExit(10000); } catch { }
            }

            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (CurrentProcessPath.StartsWith(pf, StringComparison.OrdinalIgnoreCase) || CurrentProcessPath.StartsWith(pfx86, StringComparison.OrdinalIgnoreCase) || Globals.InstalledToProgramFiles())
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "Epic_Uninstaller_Workspace");
                try 
                { 
                    if (Directory.Exists(tempDir)) SafeDeleteDirectory(tempDir, null);
                    Directory.CreateDirectory(tempDir);
                    Globals.CopyFilesRecursive(Path.GetDirectoryName(CurrentProcessPath), tempDir, true);
                } catch { }

                string tempExe = Path.Combine(tempDir, "Epic-Games_Account_Switcher.exe");
                Process.Start(new ProcessStartInfo { FileName = tempExe, Arguments = BuildUninstallArguments(Process.GetCurrentProcess().Id.ToString(), confirmed, Globals.UserDataFolder, extractDir), UseShellExecute = true, Verb = Globals.IsAdministrator ? "" : "runas" });
                Environment.Exit(0);
            }

            if (!Globals.IsAdministrator) 
            { 
                Process.Start(new ProcessStartInfo { FileName = CurrentProcessPath, Arguments = BuildUninstallArguments(waitPid, confirmed, Globals.UserDataFolder, extractDir), UseShellExecute = true, Verb = "runas" });
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
                                    using (var baseKey = vw == "64" ? RegistryKey.OpenBaseKey(hv == "HKLM" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64) : RegistryKey.OpenBaseKey(hv == "HKLM" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry32))
                                    {
                                        baseKey.DeleteSubKeyTree(RegPath, false); 
                                        baseKey.DeleteSubKeyTree(@"Software\Epic-Games_Account_Switcher", false);
                                    }
                                } catch { } 
                            }
                            _progress.UpdateProgress(40);

                            log("SHELL: Purging system shortcuts across probed desktop and start menu folders...");
                            var scName = "Epic Games Account Switcher.lnk";
                            foreach (var sm in Globals.GetProbedStartMenuDirectories()) try { File.Delete(Path.Combine(sm, scName)); } catch { }
                            foreach (var dt in Globals.GetProbedDesktopDirectories()) try { File.Delete(Path.Combine(dt, scName)); } catch { }
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
                            ScheduleDirectoryDeleteAfterExit(extractDir, log);

                            string batch = Path.Combine(Path.GetTempPath(), "epic_sw_del.bat");
                            string tempDir = Path.GetDirectoryName(CurrentProcessPath);
                            bool isTempWorkspace = tempDir.ToLower().Contains("epic_uninstaller_workspace");

                            string batchContent = string.Format(
                                "@echo off\r\n" +
                                "set count=0\r\n" +
                                ":retry\r\n" +
                                "timeout /t 1 /nobreak > nul\r\n" +
                                "del /f /q \"{0}\" 2>nul\r\n" +
                                "if not exist \"{0}\" goto done\r\n" +
                                "set /a count=count+1\r\n" +
                                "if %count% lss 25 goto retry\r\n" +
                                ":done\r\n" +
                                (isTempWorkspace ? "rd /s /q \"" + tempDir + "\" 2>nul\r\n" : "") +
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
