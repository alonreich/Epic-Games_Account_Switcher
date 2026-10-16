using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Epic_Games_Account_Switcher_Globals;
using Microsoft.Win32;

namespace Epic_Games_Account_Switcher_Client
{
    /// <summary>
    /// App lifecycle + installer/uninstaller.
    ///
    /// INSTALL (two phases, see docs/RELEASING.md):
    ///   1. User phase (runs as the signed-in user, never elevated): detect existing install, downgrade guard, ask
    ///      Upgrade/Clean, gracefully close the running app, make + VERIFY a safety backup, then request elevation.
    ///   2. Machine phase (elevated, started through the bootstrapper which extracts into an admin-only folder under
    ///      Program Files): stage new files next to the old install, swap with directory renames, write the uninstall
    ///      entry, roll back on any failure. Only after the machine phase succeeds does the user phase touch user data.
    /// UNINSTALL: the user decides whether accounts are kept (default) or deleted (with a verified backup first).
    /// No elevated code is ever executed from, or script files written to, a user-writable folder.
    /// </summary>
    public partial class App
    {
        public static string StartPage = "";
        private static Mutex _appMutex;
        private static EventWaitHandle _shutdownEvent;
        private const string AppMutexName = "{A240C23D-6F45-4E92-9979-11E6CE10A22C}";
        private const string InstallerMutexName = "Global\\EpicSwitcherSetupMutex";
        private const string MsiMutexName = "Global\\_MSIExecute";
        public const string ShutdownEventName = "Local\\EpicGamesAccountSwitcher.ShutdownRequest";
        private const string RegPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Epic-Games_Account_Switcher";
        private const string SettingsRegPath = @"Software\Epic-Games_Account_Switcher";
        private const string ShortcutName = "Epic Games Account Switcher.lnk";
        private const string AppExeName = "Epic-Games_Account_Switcher.exe";
        private const string SetupDirPrefix = "Epic-Games_Account_Switcher_Setup_";
        private const int ExitOk = 0, ExitFailed = 1, ExitCancelled = 2;
        private const int KeepSetupBackups = 3;
        private static string CurrentProcessPath => Process.GetCurrentProcess().MainModule.FileName;
        private static string LegacyDataPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Epic-Games_Account_Switcher");
        private static ProgressWindow _progress;
        private static string _extractDir;
        private static int _keepPid;

        protected override void OnExit(ExitEventArgs e)
        {
            try { Globals.WriteToLog("APP_SHUTDOWN ExitCode=" + e.ApplicationExitCode); } catch { }
            try { if (_appMutex != null) { _appMutex.ReleaseMutex(); _appMutex.Dispose(); } } catch { }
            try { if (_shutdownEvent != null) _shutdownEvent.Dispose(); } catch { }
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
            int.TryParse(GetArgValue(e.Args, "--keep-pid"), out _keepPid);
            bool isDevMode = HasArg(e.Args, "--dev");
            Globals.WriteToLog("APP_START Version=" + Globals.Version + "; Mode=" + (isDevMode ? "Dev" : "Normal") + "; ArgsCount=" + (e.Args == null ? 0 : e.Args.Length) + "; Process=" + Path.GetFileName(CurrentProcessPath));

            AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
            {
                string err = ev.ExceptionObject.ToString();
                Globals.WriteToLog("[FATAL_CRASH] " + err);
                MessageBox.Show("CRITICAL ERROR:\n\n" + err, "Epic Games Account Switcher", MessageBoxButton.OK, MessageBoxImage.Error);
            };

            bool isUninstall = HasArg(e.Args, "--uninstall");
            bool isInstall = !isDevMode && (HasArg(e.Args, "--installing") || !Globals.InstalledToProgramFiles());

            if (isUninstall)
            {
                Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                PerformUninstall(e.Args);
                return;
            }

            if (isInstall)
            {
                Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                PerformSmartInstall(e.Args);
                return;
            }

            Globals.WriteToLog("APP_LAUNCH_CONTINUE Mode=" + (isDevMode ? "Dev" : "Normal"));
            Globals.CreateDataFolder(false);
            Directory.SetCurrentDirectory(Globals.UserDataFolder);
            AccountService.CleanOrphanedTempFolders();
            IsRunningAlready(isDevMode);
            StartShutdownListener(isDevMode);
            UpdateService.CleanupOldDownloads();
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

        #region Graceful shutdown (setup asks the running app to finish its work and exit)

        /// <summary>Listens for setup's shutdown request. Any in-progress account switch is allowed to finish first.</summary>
        private static void StartShutdownListener(bool devMode)
        {
            try
            {
                _shutdownEvent = new EventWaitHandle(false, EventResetMode.ManualReset, ShutdownEventName + (devMode ? ".DEV" : ""));
                _shutdownEvent.Reset();
            }
            catch (Exception ex) { Globals.WriteToLog("[SHUTDOWN] Listener unavailable", ex); return; }

            var handle = _shutdownEvent;
            var listener = new Thread(() =>
            {
                try { handle.WaitOne(); } catch { return; }
                Globals.WriteToLog("[SHUTDOWN] Setup requested a graceful exit. Waiting for any running account switch to finish...");
                bool idle = false;
                try { idle = AccountService.SessionOperationLock.Wait(TimeSpan.FromSeconds(90)); } catch { }
                try
                {
                    Current.Dispatcher.Invoke(() =>
                    {
                        if (Current.MainWindow is MainWindow mw) mw.ExitApplication();
                        else Current.Shutdown();
                    });
                }
                catch { }
                finally { if (idle) try { AccountService.SessionOperationLock.Release(); } catch { } }
            }) { IsBackground = true, Name = "SetupShutdownListener" };
            listener.Start();
        }

        /// <summary>Asks a running app instance to close itself cleanly and waits for it (callers hard-stop afterwards as a last resort).</summary>
        private static void RequestRunningAppsToExit(Action<string> log, int timeoutSeconds = 90)
        {
            if (CountOwnedAppProcesses() == 0) { log?.Invoke("SYSTEM: The app is not running."); return; }
            EventWaitHandle ev = null;
            try
            {
                if (!EventWaitHandle.TryOpenExisting(ShutdownEventName, out ev))
                {
                    log?.Invoke("SYSTEM: The running app is an older version without graceful close support.");
                    return;
                }
                log?.Invoke("SYSTEM: Asking the running app to close. Any account switch in progress will finish first...");
                ev.Set();
                var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
                while (DateTime.UtcNow < deadline && CountOwnedAppProcesses() > 0) Thread.Sleep(500);
                log?.Invoke(CountOwnedAppProcesses() == 0 ? "SYSTEM: The app closed cleanly." : "WARNING: The app did not close in time and will be stopped.");
            }
            catch (Exception ex) { log?.Invoke("WARNING: Graceful close request failed: " + ex.Message); }
            finally
            {
                if (ev != null) { try { ev.Reset(); } catch { } try { ev.Dispose(); } catch { } }
            }
        }

        private static string[] OwnedRoots => new[] { Globals.InstallDirectory, Globals.LegacyInstallDirectoryX86 };

        private static int CountOwnedAppProcesses()
        {
            int count = 0;
            int me = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppExeName)))
            {
                using (p)
                {
                    try
                    {
                        if (p.Id == me || p.Id == _keepPid) continue;
                        string path = null;
                        try { path = p.MainModule?.FileName; } catch { }
                        if (IsOwnedProcessPath(path, OwnedRoots)) count++;
                    }
                    catch { }
                }
            }
            return count;
        }

        /// <summary>Hard-stops any process running from the install folders (last resort after the graceful request).</summary>
        private static void KillOwnedProcesses(Action<string> log)
        {
            int me = Process.GetCurrentProcess().Id;
            for (int retry = 0; retry < 3; retry++)
            {
                bool found = false;
                Process[] all;
                try { all = Process.GetProcesses(); } catch { return; }
                foreach (var p in all)
                {
                    using (p)
                    {
                        try
                        {
                            if (p.Id == me || p.Id == _keepPid) continue;
                            string path = null;
                            try { path = p.MainModule?.FileName; } catch { }
                            if (!IsOwnedProcessPath(path, OwnedRoots)) continue;
                            found = true;
                            log?.Invoke("AUTO-CLEANUP: Stopping " + p.ProcessName + " (" + p.Id + ")...");
                            p.Kill();
                            if (!p.WaitForExit(3000))
                                Process.Start(new ProcessStartInfo { FileName = "taskkill", Arguments = "/F /T /PID " + p.Id, CreateNoWindow = true, UseShellExecute = false })?.WaitForExit(2000);
                        }
                        catch { }
                    }
                }
                if (!found) break;
                Thread.Sleep(800);
            }
        }

        #endregion

        #region Helpers

        private static bool HasArg(string[] args, string name) => args != null && args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

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
            value = value ?? "";
            if (value.EndsWith("\\")) value += "\\";
            return "\"" + value.Replace("\"", "\\\"") + "\"";
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
            return ownedRoots.Any(root => IsPathUnder(processPath, root));
        }

        private static bool IsMsiBusy()
        {
            try
            {
                using (Mutex.OpenExisting(MsiMutexName)) return true;
            }
            catch { }

            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\InProgress")) { if (key != null) return true; }
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Updates\UpdateExeVolatile"))
                {
                    if (key != null && key.GetValue("Flags") is int flags && flags != 0) return true;
                }
            }
            catch { }
            return false;
        }

        private static void SleepWithTrace(int ms, string reason, Action<string> update = null)
        {
            update?.Invoke("SYSTEM: Waiting " + ms + "ms for " + reason + "...");
            Thread.Sleep(ms);
        }

        private static void SafeDeleteDirectory(string path, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
            log?.Invoke("CLEANUP: Removing folder: " + path);
            for (int i = 0; i < 5; i++)
            {
                try { Directory.Delete(path, true); return; }
                catch (Exception)
                {
                    log?.Invoke("RETRY-CLEANUP: " + Path.GetFileName(path) + " is locked. Retrying... (" + (i + 1) + "/5)");
                    KillOwnedProcesses(log);
                    Thread.Sleep(1000);
                }
            }
            try
            {
                foreach (var f in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); } catch { }
                }
                Directory.Delete(path, true);
            }
            catch (Exception ex) { log?.Invoke("WARNING: Could not fully remove " + path + ": " + ex.Message); }
        }

        /// <summary>
        /// Removes a folder after this process exits, without writing any script file (nothing an unprivileged program could tamper with).
        /// Only our own install / setup folders are accepted.
        /// </summary>
        private static void ScheduleDirectoryDeleteAfterExit(string path, Action<string> log = null, int waitForPid = 0)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
                var pfRoot = Path.GetDirectoryName(Globals.InstallDirectory);
                bool allowed = string.Equals(full, Globals.InstallDirectory, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(full, Globals.LegacyInstallDirectoryX86, StringComparison.OrdinalIgnoreCase)
                    || (string.Equals(Path.GetDirectoryName(full), pfRoot, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith(SetupDirPrefix, StringComparison.OrdinalIgnoreCase));
                if (!allowed || full.IndexOfAny(new[] { '"', '%', '&', '^' }) >= 0) { log?.Invoke("WARNING: Refusing to schedule removal of " + full); return; }
                var q = "\"" + full + "\"";
                var removeStep = "(rd /s /q " + q + " 2>nul & if not exist " + q + " exit /b 0)";
                var loopBody = waitForPid > 0
                    ? "(tasklist /FI \"PID eq " + waitForPid + "\" /NH | find \" " + waitForPid + " \" >nul || " + removeStep + ") & ping -n 2 127.0.0.1 >nul"
                    : "ping -n 2 127.0.0.1 >nul & " + removeStep;
                Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                    Arguments = "/d /c \"for /l %i in (1,1,900) do (" + loopBody + ")\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WorkingDirectory = Environment.SystemDirectory,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                log?.Invoke("CLEANUP: Scheduled removal of " + full + " after exit.");
            }
            catch (Exception ex) { log?.Invoke("WARNING: Could not schedule folder removal: " + ex.Message); }
        }

        private static CustomDialogResult AskOnUi(string title, string message, string primary, string secondary = null, string cancel = null, MessageBoxVisualStyle style = MessageBoxVisualStyle.Neutral)
        {
            var result = CustomDialogResult.Cancel;
            Current.Dispatcher.Invoke(() =>
            {
                var box = new CustomMessageBox(title, message, primary, secondary, cancel, style);
                box.ShowDialog();
                result = box.Result;
            });
            return result;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

        private static string GetDownloadsFolder()
        {
            try
            {
                if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out IntPtr p) == 0)
                {
                    try { var s = Marshal.PtrToStringUni(p); if (!string.IsNullOrWhiteSpace(s)) return s; }
                    finally { Marshal.FreeCoTaskMem(p); }
                }
            }
            catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        /// <summary>Creates a backup of all accounts and proves it can be read back. Returns null on success, or the failure reason.</summary>
        private static string CreateVerifiedBackup(string path, Action<string> log, out int profileCount)
        {
            profileCount = 0;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                AccountService.BackupAccounts(path, s => log?.Invoke("BACKUP: " + s));
                profileCount = AccountService.VerifyBackupFile(path);
                log?.Invoke("BACKUP: Verified safety backup (" + profileCount + " profiles) -> " + path);
                return null;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
                log?.Invoke("WARNING: Safety backup FAILED: " + ex.Message);
                return ex.Message;
            }
        }

        /// <summary>Keeps only the newest few automatic setup backups so the Downloads folder does not fill up.</summary>
        private static void RotateAutomaticBackups(string folder, string pattern, Action<string> log)
        {
            try
            {
                var old = new DirectoryInfo(folder).GetFiles(pattern).OrderByDescending(f => f.LastWriteTimeUtc).Skip(KeepSetupBackups).ToList();
                foreach (var f in old)
                {
                    try { f.Delete(); log?.Invoke("CLEANUP: Removed old automatic backup " + f.Name); } catch { }
                }
            }
            catch { }
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

        private static bool UninstallKeyExists()
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var key = baseKey.OpenSubKey(RegPath)) if (key != null) return true;
                }
                catch { }
            }
            return false;
        }

        private static string ReadRegistryDisplayVersion()
        {
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var key = baseKey.OpenSubKey(RegPath))
                {
                    var v = key?.GetValue("DisplayVersion") as string;
                    return AppVersion.IsValid(v) ? v : null;
                }
            }
            catch { return null; }
        }

        private static void DeleteKeyTree(RegistryHive hive, string path)
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try { using (var baseKey = RegistryKey.OpenBaseKey(hive, view)) baseKey.DeleteSubKeyTree(path, false); } catch { }
            }
        }

        private static IEnumerable<string> CommonShortcutFolders()
        {
            var list = new List<string>();
            try { var p = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms); if (!string.IsNullOrWhiteSpace(p)) list.Add(p); } catch { }
            try { var p = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory); if (!string.IsNullOrWhiteSpace(p)) list.Add(p); } catch { }
            return list;
        }

        /// <summary>Per-user shortcuts from older versions (now replaced by all-users shortcuts) are removed in the user's own context.</summary>
        private static void RemovePerUserShortcuts(Action<string> log)
        {
            var common = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in CommonShortcutFolders()) try { common.Add(Path.GetFullPath(p).TrimEnd('\\')); } catch { }
            try { common.Add(Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)).TrimEnd('\\')); } catch { }
            foreach (var dir in Globals.GetProbedStartMenuDirectories().Concat(Globals.GetProbedDesktopDirectories()))
            {
                try
                {
                    if (common.Contains(Path.GetFullPath(dir).TrimEnd('\\'))) continue;
                    var lnk = Path.Combine(dir, ShortcutName);
                    if (File.Exists(lnk)) { File.Delete(lnk); log?.Invoke("SHELL: Removed old per-user shortcut " + lnk); }
                }
                catch { }
            }
        }

        private static void LaunchInstalledApp(Action<string> log)
        {
            var exe = Globals.InstalledExePath;
            if (!File.Exists(exe)) return;
            try
            {
                if (Globals.IsAdministrator)
                    Process.Start(new ProcessStartInfo { FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), Arguments = QuoteArg(exe), UseShellExecute = false });
                else
                    Process.Start(new ProcessStartInfo { FileName = exe, WorkingDirectory = Globals.InstallDirectory, UseShellExecute = true });
            }
            catch (Exception ex) { log?.Invoke("WARNING: Could not start the app: " + ex.Message); }
        }

        private void RunWithProgress(string title, string logPrefix, Func<Action<string>, int> work)
        {
            string logFile = Globals.IsAdministrator ? null : Path.Combine(Path.GetTempPath(), logPrefix + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
            _progress = new ProgressWindow(title);
            int exitCode = ExitFailed;
            Action<string> log = msg =>
            {
                try { _progress.UpdateStatus(msg); } catch { }
                try { Globals.WriteToLog("[SETUP] " + msg); } catch { }
                if (logFile != null) { try { File.AppendAllText(logFile, "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + Environment.NewLine); } catch { } }
            };
            _progress.Loaded += (s, e) => Task.Run(() =>
            {
                try { exitCode = work(log); }
                catch (Exception ex)
                {
                    log("FATAL: " + ex.Message);
                    Globals.WriteToLog("[SETUP] Fatal error", ex);
                    AskOnUi(title.IndexOf("Uninstall", StringComparison.OrdinalIgnoreCase) >= 0 ? "Uninstall Failed" : "Setup Failed",
                        ex.Message + (logFile != null ? "\n\nSetup log: " + logFile : ""), "OK", null, null, MessageBoxVisualStyle.Error);
                    exitCode = ExitFailed;
                }
                finally { try { _progress.Dispatcher.Invoke(() => _progress.Close()); } catch { } }
            });
            _progress.ShowDialog();
            Environment.Exit(exitCode);
        }

        #endregion

        #region Install

        private sealed class InstallOptions
        {
            public bool UpdateMode;
            public bool PreserveRequested;
            public bool CleanRequested;
            public bool Stage2;
            public bool AllowDowngrade;
            public string UpgradeBackupOverride;
            public string Bootstrapper;
        }

        private void PerformSmartInstall(string[] args)
        {
            var o = new InstallOptions
            {
                UpdateMode = HasArg(args, "--update"),
                PreserveRequested = HasArg(args, "--preserve"),
                CleanRequested = HasArg(args, "--clean"),
                Stage2 = HasArg(args, "--stage2"),
                AllowDowngrade = HasArg(args, "--allow-downgrade"),
                UpgradeBackupOverride = GetArgValue(args, "--upgrade-backup"),
                Bootstrapper = GetArgValue(args, "--bootstrapper")
            };

            if (o.Stage2)
            {
                if (!Globals.IsAdministrator)
                {
                    new CustomMessageBox("Administrator Required", "Setup could not get administrator permission, so nothing was changed.", MessageBoxVisualStyle.Error).ShowDialog();
                    Environment.Exit(ExitFailed);
                    return;
                }
                RunWithProgress("Epic Games Account Switcher - Setup", "Epic_Setup_Admin", log => RunMachinePhase(o, log));
                return;
            }
            RunWithProgress("Epic Games Account Switcher - Setup", "Epic_Setup", log => RunUserPhase(o, log));
        }

        /// <summary>Phase 1: runs as the signed-in user. Owns every decision and all user data.</summary>
        private static int RunUserPhase(InstallOptions o, Action<string> log)
        {
            var current = Globals.Version;
            log("SETUP: Preparing to install version " + current + ".");
            _progress.UpdateProgress(5);

            var installedExe = Globals.InstalledExePath;
            var installedVersion = AppVersion.ReadFileVersion(installedExe) ?? ReadRegistryDisplayVersion();
            bool foundByFile = File.Exists(installedExe) || File.Exists(Path.Combine(Globals.LegacyInstallDirectoryX86, AppExeName));
            bool hasAccounts = File.Exists(Path.Combine(Globals.UserDataFolder, "EG", "accounts.json"));
            var oldDataPath = LegacyDataPath;
            bool hasLegacy = HasLegacyAccountData(oldDataPath);
            bool existing = foundByFile || hasAccounts || hasLegacy || UninstallKeyExists();
            log("SETUP: Existing install: " + (foundByFile ? "yes (" + (installedVersion ?? "unknown version") + ")" : "no") + "; saved accounts: " + (hasAccounts || hasLegacy ? "yes" : "no") + ".");

            if (!string.IsNullOrEmpty(installedVersion) && AppVersion.Compare(installedVersion, current) > 0 && !o.AllowDowngrade)
            {
                if (o.UpdateMode)
                {
                    log("CANCELLED: A newer version (" + installedVersion + ") is already installed.");
                    return ExitCancelled;
                }
                var answer = AskOnUi("Newer Version Already Installed",
                    "Version " + installedVersion + " is already installed. This setup contains the OLDER version " + current + ".\n\n" +
                    "Going back to an older version can break features and data saved by the newer one. Keep the newer version unless you have a specific reason.",
                    "Keep Version " + installedVersion, "Install Older " + current + " Anyway", null, MessageBoxVisualStyle.Error);
                if (answer != CustomDialogResult.Secondary) { log("CANCELLED: Kept the newer installed version. Nothing was changed."); return ExitCancelled; }
                o.AllowDowngrade = true;
                log("WARNING: User chose to downgrade from " + installedVersion + " to " + current + ".");
            }

            bool cleanInstall = false;
            if (o.UpdateMode || o.PreserveRequested) cleanInstall = false;
            else if (o.CleanRequested) cleanInstall = true;
            else if (existing)
            {
                var versionLine = installedVersion != null ? "Installed version: " + installedVersion + "\nThis setup: " + current + "\n\n" : "";
                var answer = AskOnUi("Existing Installation Detected",
                    versionLine +
                    "• Upgrade keeps all your saved accounts and settings (recommended).\n" +
                    "• Clean Install deletes the app AND all saved accounts, then installs fresh. A safety backup is saved to your Downloads folder first.",
                    "Upgrade & Keep Accounts", "Clean Install", "Cancel Setup");
                if (answer == CustomDialogResult.Primary) cleanInstall = false;
                else if (answer == CustomDialogResult.Secondary) cleanInstall = true;
                else { log("CANCELLED: Setup was cancelled. Nothing was changed."); return ExitCancelled; }
            }
            log("SETUP: Mode = " + (o.UpdateMode ? "in-app update" : cleanInstall ? "clean install" : existing ? "upgrade" : "fresh install") + ".");
            _progress.UpdateProgress(15);

            RequestRunningAppsToExit(log);
            KillOwnedProcesses(log);
            _progress.UpdateProgress(25);

            string backupPath = null;
            string backupError = null;
            if (hasAccounts)
            {
                backupPath = !string.IsNullOrWhiteSpace(o.UpgradeBackupOverride)
                    ? o.UpgradeBackupOverride
                    : Path.Combine(GetDownloadsFolder(), "Epic_Setup_Backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".egbackup");
                backupError = CreateVerifiedBackup(backupPath, log, out _);
                if (backupError == null) RotateAutomaticBackups(Path.GetDirectoryName(backupPath), "Epic_Setup_Backup_*.egbackup", log);
                else backupPath = null;
            }

            if (cleanInstall && hasAccounts && backupError != null)
            {
                var answer = AskOnUi("Safety Backup Failed",
                    "The safety backup of your saved accounts could NOT be created:\n" + backupError + "\n\n" +
                    "A Clean Install now would permanently delete your saved accounts with no way to get them back.",
                    "Upgrade & Keep Accounts Instead", "Delete My Accounts Permanently", "Cancel Setup", MessageBoxVisualStyle.Error);
                if (answer == CustomDialogResult.Primary) { cleanInstall = false; log("SETUP: Switched to upgrade mode to protect accounts."); }
                else if (answer == CustomDialogResult.Secondary) log("WARNING: User confirmed permanent deletion without a backup.");
                else { log("CANCELLED: Setup was cancelled. Nothing was changed."); return ExitCancelled; }
            }
            else if (backupError != null)
            {
                log("NOTICE: Continuing without a safety backup. Upgrades never touch saved accounts.");
            }
            _progress.UpdateProgress(40);

            int machine = Globals.IsAdministrator ? RunMachinePhase(o, log) : RunElevatedMachinePhase(o, log);
            if (machine != ExitOk)
            {
                log(machine == ExitCancelled ? "CANCELLED: Nothing was changed. Your accounts are untouched." : "FAILED: The previous installation was kept or restored. Your accounts are untouched.");
                if (machine == ExitFailed)
                    AskOnUi("Setup Failed", "Setup could not finish installing the new version. Your previous installation was kept (or restored) and your saved accounts were not touched.\n\nPlease try again. If it keeps failing, check the setup log in your Temp folder (Epic_Setup_*.log).", "OK", null, null, MessageBoxVisualStyle.Error);
                return machine;
            }
            _progress.UpdateProgress(80);

            if (cleanInstall)
            {
                log("CLEAN INSTALL: Removing saved accounts and settings for a fresh start...");
                SafeDeleteDirectory(Globals.UserDataFolder, log);
                SafeDeleteDirectory(oldDataPath, log);
                DeleteKeyTree(RegistryHive.CurrentUser, SettingsRegPath);
            }
            else if (hasLegacy && TryPreserveLegacyAccountData(oldDataPath, log))
            {
                SafeDeleteDirectory(oldDataPath, log);
            }
            RemovePerUserShortcuts(log);
            try { Registry.CurrentUser.DeleteSubKeyTree(RegPath, false); } catch { }
            _progress.UpdateProgress(100);

            log("SUCCESS: Epic Games Account Switcher " + current + " is installed.");
            if (backupPath != null) log("Your safety backup: " + backupPath);
            SleepWithTrace(1200, "InstallSuccessDisplay", log);
            LaunchInstalledApp(log);
            return ExitOk;
        }

        /// <summary>Starts the elevated machine phase through the bootstrapper (which extracts into an admin-only folder).</summary>
        private static int RunElevatedMachinePhase(InstallOptions o, Action<string> log)
        {
            var launcher = !string.IsNullOrWhiteSpace(o.Bootstrapper) && File.Exists(o.Bootstrapper) ? o.Bootstrapper : CurrentProcessPath;
            if (string.Equals(launcher, CurrentProcessPath, StringComparison.OrdinalIgnoreCase))
                log("WARNING: Setup was not started from the official installer EXE; elevating the extracted copy.");

            var args = new List<string> { "--installing", "--stage2" };
            if (o.AllowDowngrade) args.Add("--allow-downgrade");
            if (o.UpdateMode) args.Add("--update");

            log("SECURITY: Requesting administrator permission to update Program Files...");
            Process p;
            try
            {
                p = Process.Start(new ProcessStartInfo { FileName = launcher, Arguments = string.Join(" ", args), UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(launcher) });
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                log("CANCELLED: Administrator permission was declined.");
                return ExitCancelled;
            }
            if (p == null) return ExitFailed;

            int code = -1;
            using (p)
            {
                p.WaitForExit();
                try { code = p.ExitCode; } catch { }
            }
            log("SETUP: Administrator phase finished (code " + code + ").");
            if (code == ExitCancelled) return ExitCancelled;

            var installed = AppVersion.ReadFileVersion(Globals.InstalledExePath);
            if (installed == null || AppVersion.Compare(installed, Globals.Version) != 0)
            {
                log("ERROR: Installed version check failed (found " + (installed ?? "none") + ", expected " + Globals.Version + ").");
                return ExitFailed;
            }
            return ExitOk;
        }

        /// <summary>Phase 2: elevated. Atomic, staged replacement of the program files with rollback. Never touches user data.</summary>
        private static int RunMachinePhase(InstallOptions o, Action<string> log)
        {
            using (var setupMutex = new Mutex(false, InstallerMutexName))
            {
                bool owned = false;
                try { owned = setupMutex.WaitOne(TimeSpan.FromSeconds(10), false); } catch (AbandonedMutexException) { owned = true; }
                if (!owned)
                {
                    AskOnUi("Installer Busy", "Another installation is already in progress. Please wait for it to finish.", "OK");
                    return ExitCancelled;
                }
                try
                {
                    if (IsMsiBusy())
                    {
                        AskOnUi("System Busy", "Windows Installer is busy or a reboot is pending. Please wait and try again.", "OK");
                        return ExitCancelled;
                    }

                    var targetDir = Globals.InstallDirectory;
                    var installedVersion = AppVersion.ReadFileVersion(Globals.InstalledExePath);
                    if (!o.AllowDowngrade && installedVersion != null && AppVersion.Compare(installedVersion, Globals.Version) > 0)
                    {
                        log("CANCELLED: A newer version (" + installedVersion + ") is installed and downgrade was not confirmed.");
                        return ExitCancelled;
                    }

                    var sourceDir = Path.GetFullPath(Globals.AppDataFolder).TrimEnd(Path.DirectorySeparatorChar);
                    if (string.Equals(sourceDir, targetDir, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Setup cannot run from the install folder itself.");
                    _progress.UpdateProgress(45);

                    log("SYSTEM AUDIT: Making sure the app is closed...");
                    RequestRunningAppsToExit(log, 30);
                    KillOwnedProcesses(log);
                    try { Process.Start(new ProcessStartInfo { FileName = "schtasks", Arguments = "/Delete /TN \"Epic-Games_Account_Switcher\" /F", CreateNoWindow = true, UseShellExecute = false })?.WaitForExit(2000); } catch { }
                    _progress.UpdateProgress(50);

                    var payloadFiles = EnumeratePayloadFiles(sourceDir).ToList();
                    if (!payloadFiles.Any(f => string.Equals(f.Key, AppExeName, StringComparison.OrdinalIgnoreCase)))
                        throw new FileNotFoundException("The installer payload is incomplete (" + AppExeName + " missing). Please download the installer again.");
                    long payloadBytes = payloadFiles.Sum(f => f.Value.Length);
                    EnsureFreeSpace(targetDir, payloadBytes * 2 + 50L * 1024 * 1024);

                    var pfRoot = Path.GetDirectoryName(targetDir);
                    PurgeStaleStagingFolders(pfRoot, log);
                    var token = Guid.NewGuid().ToString("N").Substring(0, 8);
                    var staging = targetDir + ".new_" + token;
                    var previous = targetDir + ".old_" + token;

                    log("DEPLOYMENT: Copying " + payloadFiles.Count + " files to a staging folder...");
                    if (!CopyAndVerifyPayload(staging, payloadFiles, log))
                    {
                        SafeDeleteDirectory(staging, null);
                        throw new IOException("Could not copy the new program files (disk full, or blocked by antivirus?). Your existing installation was not touched.");
                    }
                    _progress.UpdateProgress(60);

                    bool hadPrevious = Directory.Exists(targetDir);
                    if (hadPrevious && !TryMoveDirectory(targetDir, previous, log))
                    {
                        SafeDeleteDirectory(staging, null);
                        throw new IOException("The current installation is locked by another program, so it could not be replaced. Nothing was changed. Close any program using it and try again.");
                    }
                    if (!TryMoveDirectory(staging, targetDir, log))
                    {
                        if (hadPrevious) TryMoveDirectory(previous, targetDir, log);
                        SafeDeleteDirectory(staging, null);
                        throw new IOException("Could not activate the new version. The previous version was restored.");
                    }
                    log("DEPLOYMENT: New version activated.");
                    _progress.UpdateProgress(70);

                    try { WriteUninstallEntry(targetDir, payloadBytes); }
                    catch (Exception ex)
                    {
                        log("ERROR: Could not register the app with Windows (" + ex.Message + "). Rolling back...");
                        SafeDeleteDirectory(targetDir, log);
                        if (hadPrevious) TryMoveDirectory(previous, targetDir, log);
                        throw;
                    }
                    try { using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)) baseKey.DeleteSubKeyTree(RegPath, false); } catch { }
                    DeleteKeyTree(RegistryHive.LocalMachine, SettingsRegPath);
                    _progress.UpdateProgress(80);

                    CreateAllUsersShortcuts(log);
                    if (hadPrevious) SafeDeleteDirectory(previous, log);
                    SafeDeleteDirectory(Globals.LegacyInstallDirectoryX86, log);
                    _progress.UpdateProgress(100);
                    log("SUCCESS: Program files for version " + Globals.Version + " are in place.");
                    return ExitOk;
                }
                finally { try { setupMutex.ReleaseMutex(); } catch { } }
            }
        }

        private static IEnumerable<KeyValuePair<string, FileInfo>> EnumeratePayloadFiles(string sourceDir)
        {
            foreach (var f in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var rel = f.Substring(sourceDir.Length).TrimStart(Path.DirectorySeparatorChar);
                if (string.Equals(rel, "payload.zip", StringComparison.OrdinalIgnoreCase)) continue;
                if (rel.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
                yield return new KeyValuePair<string, FileInfo>(rel, new FileInfo(f));
            }
        }

        private static bool CopyAndVerifyPayload(string staging, List<KeyValuePair<string, FileInfo>> files, Action<string> log)
        {
            try
            {
                Directory.CreateDirectory(staging);
                foreach (var f in files)
                {
                    var dest = Path.Combine(staging, f.Key);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    File.Copy(f.Value.FullName, dest, true);
                    if (new FileInfo(dest).Length != f.Value.Length) throw new IOException("Size mismatch after copy: " + f.Key);
                }
                return true;
            }
            catch (Exception ex)
            {
                log("ERROR: Staging copy failed: " + ex.Message);
                return false;
            }
        }

        private static bool TryMoveDirectory(string from, string to, Action<string> log)
        {
            for (int i = 0; i < 6; i++)
            {
                try { Directory.Move(from, to); return true; }
                catch (Exception ex)
                {
                    log("RETRY: Folder is in use (" + ex.Message + "). Attempt " + (i + 1) + "/6...");
                    KillOwnedProcesses(log);
                    Thread.Sleep(1000);
                }
            }
            return false;
        }

        private static void EnsureFreeSpace(string targetDir, long neededBytes)
        {
            DriveInfo drive;
            try { drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(targetDir))); }
            catch { return; }
            if (drive.IsReady && drive.AvailableFreeSpace < neededBytes)
                throw new IOException("Not enough free disk space on " + drive.Name + ". Setup needs about " + (neededBytes / (1024 * 1024)) + " MB free. Nothing was changed.");
        }

        private static void PurgeStaleStagingFolders(string pfRoot, Action<string> log)
        {
            try
            {
                var name = Globals.InstallFolderName;
                foreach (var dir in Directory.GetDirectories(pfRoot, name + ".*_*", SearchOption.TopDirectoryOnly)
                    .Concat(Directory.GetDirectories(pfRoot, SetupDirPrefix + "*", SearchOption.TopDirectoryOnly)))
                {
                    if (!string.IsNullOrWhiteSpace(_extractDir) && string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), Path.GetFullPath(_extractDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), Path.GetFullPath(Globals.AppDataFolder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                    try { if (Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddHours(-1)) SafeDeleteDirectory(dir, log); } catch { }
                }
            }
            catch { }
        }

        private static void WriteUninstallEntry(string targetDir, long payloadBytes)
        {
            var exe = Path.Combine(targetDir, AppExeName);
            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = baseKey.CreateSubKey(RegPath))
            {
                if (key == null) throw new IOException("Could not create the uninstall registry key.");
                key.SetValue("DisplayName", "Epic Games Account Switcher");
                key.SetValue("UninstallString", QuoteArg(exe) + " --uninstall");
                key.SetValue("DisplayIcon", exe);
                key.SetValue("DisplayVersion", Globals.Version);
                key.SetValue("InstallLocation", targetDir);
                key.SetValue("Publisher", "Epic Games Account Switcher Team");
                key.SetValue("URLInfoAbout", "https://github.com/" + UpdateService.RepositoryOwner + "/" + UpdateService.RepositoryName);
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                key.SetValue("EstimatedSize", (int)Math.Max(1, payloadBytes / 1024), RegistryValueKind.DWord);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        private static void CreateAllUsersShortcuts(Action<string> log)
        {
            try
            {
                var exe = Globals.InstalledExePath;
                foreach (var dir in CommonShortcutFolders())
                {
                    var path = Path.Combine(dir, ShortcutName);
                    Globals.CreateShortcut(exe, path, "Epic Games Account Switcher");
                    log("SHELL: Shortcut -> " + path);
                }
                try { var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), ShortcutName); if (File.Exists(legacy)) File.Delete(legacy); } catch { }
            }
            catch (Exception ex) { log("WARNING: Shortcut creation failed: " + ex.Message); }
        }

        #endregion

        #region Uninstall

        private void PerformUninstall(string[] args)
        {
            if (HasArg(args, "--machine-only"))
            {
                if (!Globals.IsAdministrator) { Environment.Exit(ExitFailed); return; }
                RunWithProgress("Epic Games Account Switcher - Uninstall", "Epic_Uninstall_Admin", log => RunUninstallMachinePhase(log));
                return;
            }

            var choice = new CustomMessageBox("Uninstall Epic Games Account Switcher",
                "Your saved accounts are KEPT by default, so reinstalling later brings everything back.\n\nWhat would you like to remove?",
                "Remove App, Keep My Accounts", "Remove App and All Accounts", "Cancel");
            choice.ShowDialog();
            if (choice.Result != CustomDialogResult.Primary && choice.Result != CustomDialogResult.Secondary) { Environment.Exit(ExitCancelled); return; }
            bool purge = choice.Result == CustomDialogResult.Secondary;
            if (purge)
            {
                var confirm = new CustomMessageBox("Delete All Accounts?",
                    "This permanently deletes every saved account profile from this PC.\n\nA safety backup will be saved to your Downloads folder first, so you can bring them back later with RESTORE.",
                    "Delete Everything", "Keep My Accounts", "Cancel", MessageBoxVisualStyle.Error);
                confirm.ShowDialog();
                if (confirm.Result == CustomDialogResult.Secondary) purge = false;
                else if (confirm.Result != CustomDialogResult.Primary) { Environment.Exit(ExitCancelled); return; }
            }
            RunWithProgress("Epic Games Account Switcher - Uninstall", "Epic_Uninstall", log => RunUninstallUserPhase(purge, log));
        }

        private static int RunUninstallUserPhase(bool purge, Action<string> log)
        {
            _progress.UpdateProgress(5);
            RequestRunningAppsToExit(log);
            KillOwnedProcesses(log);
            _progress.UpdateProgress(20);

            string backupPath = null;
            bool hasAccounts = File.Exists(Path.Combine(Globals.UserDataFolder, "EG", "accounts.json"));
            if (purge && hasAccounts)
            {
                backupPath = Path.Combine(GetDownloadsFolder(), "Epic_Uninstall_Backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".egbackup");
                var error = CreateVerifiedBackup(backupPath, log, out _);
                if (error != null)
                {
                    backupPath = null;
                    var answer = AskOnUi("Safety Backup Failed",
                        "The safety backup could NOT be created:\n" + error + "\n\nDeleting now would permanently remove your saved accounts.",
                        "Keep My Accounts", "Delete Permanently", "Cancel", MessageBoxVisualStyle.Error);
                    if (answer == CustomDialogResult.Primary) purge = false;
                    else if (answer != CustomDialogResult.Secondary) return ExitCancelled;
                }
                else RotateAutomaticBackups(Path.GetDirectoryName(backupPath), "Epic_Uninstall_Backup_*.egbackup", log);
            }
            _progress.UpdateProgress(40);

            int machine;
            if (Globals.IsAdministrator) machine = RunUninstallMachinePhase(log);
            else
            {
                var exe = File.Exists(Globals.InstalledExePath) ? Globals.InstalledExePath : CurrentProcessPath;
                log("SECURITY: Requesting administrator permission to remove program files...");
                try
                {
                    using (var p = Process.Start(new ProcessStartInfo { FileName = exe, Arguments = "--uninstall --machine-only --keep-pid " + Process.GetCurrentProcess().Id, UseShellExecute = true, Verb = "runas" }))
                    {
                        p?.WaitForExit();
                        machine = UninstallKeyExists() ? ExitFailed : ExitOk;
                    }
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    log("CANCELLED: Administrator permission was declined. Nothing was removed.");
                    return ExitCancelled;
                }
            }
            if (machine != ExitOk)
            {
                AskOnUi("Uninstall Failed", "The program files could not be removed. Your accounts were not touched.", "OK", null, null, MessageBoxVisualStyle.Error);
                return machine;
            }
            _progress.UpdateProgress(75);

            if (purge)
            {
                log("CLEANUP: Deleting saved accounts and settings...");
                SafeDeleteDirectory(Globals.UserDataFolder, log);
                SafeDeleteDirectory(LegacyDataPath, log);
                DeleteKeyTree(RegistryHive.CurrentUser, SettingsRegPath);
            }
            else log("CLEANUP: Saved accounts were kept in " + Globals.UserDataFolder);
            RemovePerUserShortcuts(log);
            try { Registry.CurrentUser.DeleteSubKeyTree(RegPath, false); } catch { }
            _progress.UpdateProgress(100);
            log("SUCCESS: Uninstall complete.");

            AskOnUi("Uninstall Complete",
                "Epic Games Account Switcher was removed." +
                (purge ? (backupPath != null ? "\n\nA backup of your accounts was saved to:\n" + backupPath : "") : "\n\nYour saved accounts were kept. Reinstall at any time to get them back."),
                "OK", null, null, MessageBoxVisualStyle.Success);
            return ExitOk;
        }

        /// <summary>Elevated: removes program files, the Windows uninstall entry and all-users shortcuts. Never touches user data.</summary>
        private static int RunUninstallMachinePhase(Action<string> log)
        {
            using (var setupMutex = new Mutex(false, InstallerMutexName))
            {
                bool owned = false;
                try { owned = setupMutex.WaitOne(TimeSpan.FromSeconds(10), false); } catch (AbandonedMutexException) { owned = true; }
                if (!owned) { log("BUSY: Another setup is running."); return ExitFailed; }
                try
                {
                    log("SYSTEM AUDIT: Making sure the app is closed...");
                    RequestRunningAppsToExit(log, 30);
                    KillOwnedProcesses(log);
                    try { Process.Start(new ProcessStartInfo { FileName = "schtasks", Arguments = "/Delete /TN \"Epic-Games_Account_Switcher\" /F", CreateNoWindow = true, UseShellExecute = false })?.WaitForExit(2000); } catch { }
                    _progress.UpdateProgress(50);

                    log("REGISTRY: Removing the Windows uninstall entry...");
                    DeleteKeyTree(RegistryHive.LocalMachine, RegPath);
                    DeleteKeyTree(RegistryHive.LocalMachine, SettingsRegPath);

                    log("SHELL: Removing shortcuts...");
                    foreach (var dir in CommonShortcutFolders()) try { File.Delete(Path.Combine(dir, ShortcutName)); } catch { }
                    try { File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), ShortcutName)); } catch { }
                    _progress.UpdateProgress(70);

                    log("FILESYSTEM: Removing program files...");
                    SafeDeleteDirectory(Globals.LegacyInstallDirectoryX86, log);
                    // The uninstall window (and this process) may run from the install folder, so the folder is removed only
                    // after they exit: deleting files earlier could pull assemblies out from under the still-open window.
                    if (IsPathUnder(CurrentProcessPath, Globals.InstallDirectory) || _keepPid != 0)
                        ScheduleDirectoryDeleteAfterExit(Globals.InstallDirectory, log, _keepPid != 0 ? _keepPid : Process.GetCurrentProcess().Id);
                    else SafeDeleteDirectory(Globals.InstallDirectory, log);
                    _progress.UpdateProgress(100);
                    log("SUCCESS: Program files removed.");
                    return ExitOk;
                }
                finally { try { setupMutex.ReleaseMutex(); } catch { } }
            }
        }

        #endregion
    }
}
