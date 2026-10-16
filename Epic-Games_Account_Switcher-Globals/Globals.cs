using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Drawing;
using System.Collections.Concurrent;

namespace Epic_Games_Account_Switcher_Globals
{
    public partial class Globals
    {
        public static bool VerboseMode;
        public static readonly string Version = AppVersion.Current;
        public const string InstallFolderName = "Epic-Games_Account_Switcher";
        public static string InstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), InstallFolderName);
        public static string LegacyInstallDirectoryX86 => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), InstallFolderName);
        public static string InstalledExePath => Path.Combine(InstallDirectory, "Epic-Games_Account_Switcher.exe");
        public static readonly string LogSessionId = Guid.NewGuid().ToString("N").Substring(0, 12);
        public static string AppDataFolder => AppDomain.CurrentDomain.BaseDirectory;
        public static string UserDataFolderOverride { get; set; }
        public static string LogFolderOverride { get; set; }
        private const long MaxLogFileBytes = 10L * 1024L * 1024L;
        private const long MaxTotalLogBytes = 40L * 1024L * 1024L;
        private const int MaxLogFiles = 8;
        private const int MaxLogAgeDays = 30;
        private static readonly object LogWriteLock = new object();
        private const string LogMutexName = "EpicGamesAccountSwitcherLogMutex";
        public static string UserDataFolder
        {
            get
            {
                var p = string.IsNullOrWhiteSpace(UserDataFolderOverride)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EpicAccountSwitcher")
                    : Path.GetFullPath(UserDataFolderOverride);
                try { if (!Directory.Exists(p)) Directory.CreateDirectory(p); } catch { }
                return p;
            }
        }
        public static bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        public static string GetLogPath()
        {
            try
            {
                return Path.Combine(GetLogDirectory(), "Epic-Games_Account_Switcher.log");
            } catch { return Path.Combine(UserDataFolder, "Epic-Games_Account_Switcher.log"); }
        }

        public static string GetLogDirectory()
        {
            var ld = string.IsNullOrWhiteSpace(LogFolderOverride)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic-Games_Account_Switcher")
                : Path.GetFullPath(LogFolderOverride);
            if (!Directory.Exists(ld)) Directory.CreateDirectory(ld);
            return ld;
        }

        public static void WriteToLog(string msg, Exception ex = null)
        {
            try
            {
                string c = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] [v" + Version + "] [session:" + LogSessionId + "] " + msg + (ex != null ? "\n[EX] " + ex.Message + "\n" + ex.StackTrace : "") + Environment.NewLine;
                c = RedactSensitiveLogText(c);
                AppendToBoundedLogFile(GetLogPath(), c);
                if (VerboseMode) Console.WriteLine("[LOG] " + RedactSensitiveLogText(msg));
            } catch { }
        }

        public static void AppendToBoundedLogFile(string path, string text)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || text == null) return;
                using (var mutex = new Mutex(false, LogMutexName))
                {
                    var locked = false;
                    try
                    {
                        try { locked = mutex.WaitOne(TimeSpan.FromSeconds(2), false); } catch (AbandonedMutexException) { locked = true; }
                        if (!locked) return;
                        lock (LogWriteLock)
                        {
                            AppendToBoundedLogFileUnsafe(path, RedactSensitiveLogText(text));
                        }
                    }
                    finally
                    {
                        if (locked) try { mutex.ReleaseMutex(); } catch { }
                    }
                }
            }
            catch { }
        }

        private static void AppendToBoundedLogFileUnsafe(string path, string text)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            RotateIfNeeded(path, text.Length);
            File.AppendAllText(path, text, Encoding.UTF8);
            CleanupLogFiles(path);
        }

        private static void RotateIfNeeded(string path, int incomingChars)
        {
            try
            {
                if (!File.Exists(path)) return;
                var length = new FileInfo(path).Length;
                if (length + Math.Max(1, incomingChars * 2L) < MaxLogFileBytes) return;

                var dir = Path.GetDirectoryName(path);
                var name = Path.GetFileNameWithoutExtension(path);
                var ext = Path.GetExtension(path);
                var rotated = Path.Combine(dir, name + "." + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ext);
                File.Move(path, rotated);
            }
            catch { }
        }

        private static void CleanupLogFiles(string activePath)
        {
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(activePath));
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
                var prefix = Path.GetFileNameWithoutExtension(activePath);
                var files = Directory.GetFiles(dir, prefix + "*.log")
                    .Select(f => new FileInfo(f))
                    .Where(f => f.Exists)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToList();

                var cutoff = DateTime.UtcNow.AddDays(-MaxLogAgeDays);
                foreach (var file in files.Where(f => f.LastWriteTimeUtc < cutoff).ToList())
                {
                    TryDeleteLogFile(file);
                    files.Remove(file);
                }

                foreach (var file in files.Skip(MaxLogFiles).ToList())
                {
                    TryDeleteLogFile(file);
                    files.Remove(file);
                }

                long total = files.Sum(f => f.Exists ? f.Length : 0L);
                foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc).ToList())
                {
                    if (total <= MaxTotalLogBytes) break;
                    var len = file.Exists ? file.Length : 0L;
                    if (string.Equals(file.FullName, Path.GetFullPath(activePath), StringComparison.OrdinalIgnoreCase)) continue;
                    TryDeleteLogFile(file);
                    total -= len;
                }
            }
            catch { }
        }

        private static void TryDeleteLogFile(FileInfo file)
        {
            try { if (file != null && file.Exists) file.Delete(); } catch { }
        }

        public static string RedactSensitiveLogText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            try
            {
                text = Regex.Replace(text, @"--(user-data|log-dir|extract-dir)\s+""[^""]*""", "--$1 \"[path-redacted]\"", RegexOptions.IgnoreCase);
                text = Regex.Replace(text, @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", "[email-redacted]");
                text = Regex.Replace(text, @"\b[a-fA-F0-9]{16,}\b", "[id-redacted]");
                text = Regex.Replace(text, @"([A-Za-z]:\\)(?:[^\\\r\n]+\\)+([^\\\r\n]+)", "$1...\\$2");
            }
            catch { }
            return text;
        }

        public static string SafeLogId(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "NULL";
            var clean = RedactSensitiveLogText(value);
            if (clean == "[id-redacted]") return clean;
            return clean.Length <= 6 ? clean : clean.Substring(0, 3) + "..." + clean.Substring(clean.Length - 3);
        }
        public static void ForceDeleteDirectory(string p)
        {
            if (string.IsNullOrWhiteSpace(p) || !Directory.Exists(p)) return;
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    try
                    {
                        var stack = new Stack<string>();
                        stack.Push(p);
                        while (stack.Count > 0)
                        {
                            var dir = stack.Pop();
                            try
                            {
                                var files = Directory.GetFiles(dir);
                                foreach (var file in files)
                                {
                                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                                    try { File.Delete(file); }
                                    catch
                                    {
                                        try { var longFile = file.StartsWith(@"\\") ? file : @"\\?\" + file; File.SetAttributes(longFile, FileAttributes.Normal); File.Delete(longFile); } catch { }
                                    }
                                }
                                foreach (var sub in Directory.GetDirectories(dir)) stack.Push(sub);
                            }
                            catch { }
                        }
                    }
                    catch { }

                    try { Directory.Delete(p, true); }
                    catch
                    {
                        try { var longDir = p.StartsWith(@"\\") ? p : @"\\?\" + p; Directory.Delete(longDir, true); } catch { }
                    }
                    if (!Directory.Exists(p)) return;
                }
                catch { }
                Thread.Sleep(200 * (i + 1));
            }

            if (Directory.Exists(p))
            {
                try
                {
                    var orphanedName = p + "_orph_del_" + Guid.NewGuid().ToString("N").Substring(0, 6);
                    Directory.Move(p, orphanedName);
                    WriteToLog("[IO] ForceDeleteDirectory could not delete immediately due to open handles; renamed to: " + orphanedName);
                }
                catch (Exception ex)
                {
                    WriteToLog("[IO] ForceDeleteDirectory failed to delete or rename directory: " + p, ex);
                }
            }
        }
        public static void CreateDataFolder(bool overwrite)
        {
            if (AppDataFolder == UserDataFolder) return;
            if (InstalledToProgramFiles() && !IsAdministrator) return;
            var sp = Path.Combine(UserDataFolder, "appsettings.json");
            if (!File.Exists(sp)) { var src = Path.Combine(AppDataFolder, "appsettings.json"); if (File.Exists(src)) CopyFile(src, sp, false); }
        }
        /// <summary>True only when running from the real install folder (not from setup/uninstall staging folders under Program Files).</summary>
        public static bool InstalledToProgramFiles()
        {
            try
            {
                var dir = Path.GetFullPath(AppDataFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(dir, Path.GetFullPath(InstallDirectory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(dir, Path.GetFullPath(LegacyInstallDirectoryX86).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
        public static bool IsProcessRunning(string n)
        {
            try
            {
                var curId = Process.GetCurrentProcess().Id;
                var procs = Process.GetProcessesByName(n);
                var found = procs.Any(p => p.Id != curId);
                foreach (var p in procs) { try { p.Dispose(); } catch { } }
                return found;
            }
            catch { return false; }
        }
        public static bool CopyFile(string s, string d, bool ovr = true)
        {
            if (!File.Exists(s)) return false;
            try { Directory.CreateDirectory(Path.GetDirectoryName(d)); File.Copy(s, d, ovr); return true; } catch { return false; }
        }
        public static async Task<T> RetryAsync<T>(Func<Task<T>> a, int max = 5, int del = 500, bool th = false, CancellationToken cancellationToken = default(CancellationToken))
        {
            for (int i = 0; i < max; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { return await a(); }
                catch
                {
                    if (i == max - 1 && th) throw;
                    await Task.Delay(del * (i + 1), cancellationToken);
                }
            }
            return default(T);
        }
        public static async Task RetryActionAsync(Func<Task> a, int max = 5, int del = 500, bool th = false, CancellationToken cancellationToken = default(CancellationToken))
        {
            for (int i = 0; i < max; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { await a(); return; }
                catch
                {
                    if (i == max - 1 && th) throw;
                    await Task.Delay(del * (i + 1), cancellationToken);
                }
            }
        }
        public static string ReadAllText(string f) { using (FileStream fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) using (StreamReader tr = new StreamReader(fs)) return tr.ReadToEnd(); }
        public static async Task<bool> CopyFilesRecursiveAsync(string s, string d, bool ovr = true, List<string> ex = null)
        {
            return await Task.Run(() => CopyFilesRecursive(s, d, ovr, ex));
        }
        public static bool CopyFilesRecursive(string s, string d, bool ovr = true, List<string> ex = null)
        {
            try
            {
                s = Path.GetFullPath(s).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                d = Path.GetFullPath(d).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                string sLong = s.StartsWith(@"\\") ? s : @"\\?\" + s;
                string dLong = d.StartsWith(@"\\") ? d : @"\\?\" + d;

                if (!Directory.Exists(dLong)) Directory.CreateDirectory(dLong);

                var dirs = new Stack<string>();
                dirs.Push(sLong);

                while (dirs.Count > 0)
                {
                    var currentDir = dirs.Pop();
                    string relDir = currentDir.Substring(sLong.Length).TrimStart(Path.DirectorySeparatorChar);
                    if (ex != null && ex.Any(e => relDir.IndexOf(e, StringComparison.OrdinalIgnoreCase) >= 0)) continue;

                    string targetSubDir = Path.Combine(dLong, relDir);
                    try { Directory.CreateDirectory(targetSubDir); } catch { }

                    string[] subDirs = new string[0];
                    try { subDirs = Directory.GetDirectories(currentDir); } catch { }
                    foreach (var sd in subDirs) dirs.Push(sd);

                    string[] files = new string[0];
                    try { files = Directory.GetFiles(currentDir); } catch { }
                    foreach (var f in files)
                    {
                        string r = f.Substring(sLong.Length).TrimStart(Path.DirectorySeparatorChar);
                        if (ex != null && ex.Any(e => r.IndexOf(e, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                        string dest = Path.Combine(dLong, r);
                        if (ovr || !File.Exists(dest))
                        {
                            try
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                                if (File.Exists(dest)) try { File.SetAttributes(dest, FileAttributes.Normal); } catch { }
                                try { File.Copy(f, dest, ovr); }
                                catch
                                {
                                    var fLong = f.StartsWith(@"\\") ? f : @"\\?\" + f;
                                    var destLong = dest.StartsWith(@"\\") ? dest : @"\\?\" + dest;
                                    if (File.Exists(destLong)) try { File.SetAttributes(destLong, FileAttributes.Normal); } catch { }
                                    File.Copy(fLong, destLong, ovr);
                                }
                            }
                            catch { }
                        }
                    }
                }
                return true;
            }
            catch (Exception exn)
            {
                WriteToLog("[IO] Recursive copy failed: " + s + " -> " + d, exn);
                return false;
            }
        }
        public static void CreateShortcut(string target, string path, string desc, string args = "", string icon = "")
        {
            try {
                var f = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(f)) Directory.CreateDirectory(f);
                var tmpPath = Path.Combine(Path.GetTempPath(), "sc_" + Guid.NewGuid().ToString("N") + ".lnk");
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return;
                object s = Activator.CreateInstance(t);
                object sc = s.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, s, new object[] { tmpPath });
                sc.GetType().InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
                sc.GetType().InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { desc });
                sc.GetType().InvokeMember("Arguments", BindingFlags.SetProperty, null, sc, new object[] { args });
                sc.GetType().InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(target) });
                if (!string.IsNullOrEmpty(icon)) sc.GetType().InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { icon });
                sc.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
                if (File.Exists(tmpPath))
                {
                    for (int i = 0; i < 5; i++)
                    {
                        try
                        {
                            File.Copy(tmpPath, path, true);
                            break;
                        }
                        catch
                        {
                            if (i == 4) throw;
                            Thread.Sleep(300 * (i + 1));
                        }
                    }
                    try { File.Delete(tmpPath); } catch { }
                }
            } catch (Exception ex) { WriteToLog("[SHELL] Failed to create shortcut: " + path, ex); }
        }

        public static List<string> GetProbedDesktopDirectories()
        {
            var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void AddIfValid(string p) {
                if (!string.IsNullOrWhiteSpace(p)) {
                    try {
                        var exp = Environment.ExpandEnvironmentVariables(p.Trim());
                        if (Directory.Exists(exp)) results.Add(Path.GetFullPath(exp));
                    } catch { }
                }
            };

            AddIfValid(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
            AddIfValid(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            AddIfValid(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));

            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders"))
                {
                    if (key != null)
                    {
                        AddIfValid(key.GetValue("{754AC886-DF64-4CBA-86B5-F7FBF4FBCEF5}") as string);
                        AddIfValid(key.GetValue("Desktop") as string);
                    }
                }
            }
            catch { }

            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders"))
                {
                    if (key != null)
                    {
                        AddIfValid(key.GetValue("Desktop") as string);
                    }
                }
            }
            catch { }

            try
            {
                var userProf = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                AddIfValid(Path.Combine(userProf, "Desktop"));

                foreach (var envVar in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
                {
                    var odPath = Environment.GetEnvironmentVariable(envVar);
                    if (!string.IsNullOrWhiteSpace(odPath)) AddIfValid(Path.Combine(odPath, "Desktop"));
                }

                if (Directory.Exists(userProf))
                {
                    foreach (var odDir in Directory.GetDirectories(userProf, "OneDrive*", SearchOption.TopDirectoryOnly))
                    {
                        AddIfValid(Path.Combine(odDir, "Desktop"));
                    }
                }
            }
            catch { }

            return results.ToList();
        }

        public static List<string> GetProbedStartMenuDirectories()
        {
            var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void AddIfValid(string p) {
                if (!string.IsNullOrWhiteSpace(p)) {
                    try {
                        var exp = Environment.ExpandEnvironmentVariables(p.Trim());
                        if (Directory.Exists(exp)) results.Add(Path.GetFullPath(exp));
                    } catch { }
                }
            };

            AddIfValid(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms));
            AddIfValid(Environment.GetFolderPath(Environment.SpecialFolder.Programs));
            AddIfValid(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu));
            AddIfValid(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu));

            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders"))
                {
                    if (key != null)
                    {
                        AddIfValid(key.GetValue("Programs") as string);
                        AddIfValid(key.GetValue("Start Menu") as string);
                    }
                }
            }
            catch { }

            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders"))
                {
                    if (key != null)
                    {
                        AddIfValid(key.GetValue("Programs") as string);
                        AddIfValid(key.GetValue("Start Menu") as string);
                    }
                }
            }
            catch { }

            return results.ToList();
        }
    }
}
