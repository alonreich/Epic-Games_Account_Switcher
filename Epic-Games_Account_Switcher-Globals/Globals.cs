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
        public static readonly string Version = "2026.03.30";
        public static string AppDataFolder => AppDomain.CurrentDomain.BaseDirectory;
        public static string UserDataFolder
        {
            get { var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EpicAccountSwitcher"); try { if (!Directory.Exists(p)) Directory.CreateDirectory(p); } catch { } return p; }
        }
        public static bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        public static string GetLogPath()
        {
            try
            {
                var ld = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic-Games_Account_Switcher");
                if (!Directory.Exists(ld)) Directory.CreateDirectory(ld);
                return Path.Combine(ld, "Epic-Games_Account_Switcher.log");
            } catch { return Path.Combine(UserDataFolder, "Epic-Games_Account_Switcher.log"); }
        }
        public static void WriteToLog(string msg, Exception ex = null)
        {
            try
            {
                string lp = GetLogPath();
                string c = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + msg + (ex != null ? "\n[EX] " + ex.Message + "\n" + ex.StackTrace : "") + "\n";
                File.AppendAllText(lp, c);
                if (VerboseMode) Console.WriteLine("[LOG] " + msg);
            } catch { }
        }
        public static void DebugWriteLine(string msg) { if (VerboseMode) WriteToLog("[DEBUG] " + msg); }
        public static void ForceDeleteDirectory(string p)
        {
            if (!Directory.Exists(p)) return;
            for (int i = 0; i < 5; i++)
            {
                try { var d = new DirectoryInfo(p) { Attributes = FileAttributes.Normal }; foreach (var info in d.GetFileSystemInfos("*", SearchOption.AllDirectories)) info.Attributes = FileAttributes.Normal; d.Delete(true); return; }
                catch { if (i == 4) throw; Thread.Sleep(500 * (i + 1)); }
            }
        }
        public static void CreateDataFolder(bool overwrite)
        {
            if (AppDataFolder == UserDataFolder) return;
            if (InstalledToProgramFiles() && !IsAdministrator) return;
            var sp = Path.Combine(UserDataFolder, "appsettings.json");
            if (!File.Exists(sp)) { var src = Path.Combine(AppDataFolder, "appsettings.json"); if (File.Exists(src)) CopyFile(src, sp, false); }
        }
        public static bool InstalledToProgramFiles()
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            return AppDataFolder.StartsWith(pf, StringComparison.OrdinalIgnoreCase) || AppDataFolder.StartsWith(pfx86, StringComparison.OrdinalIgnoreCase);
        }
        public static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try { File.WriteAllText(Path.Combine(UserDataFolder, "LastError.txt"), e.ExceptionObject.ToString()); } catch { }
        }
        public static bool IsProcessRunning(string n)
        {
            try { return Process.GetProcessesByName(n).Any(p => p.Id != Process.GetCurrentProcess().Id); } catch { return false; }
        }
        public static void KillProcess(string n, bool wait = true)
        {
            try
            {
                var curId = Process.GetCurrentProcess().Id;
                for (int i = 0; i < 3; i++)
                {
                    var procs = Process.GetProcessesByName(n).Where(p => p.Id != curId).ToList();
                    if (!procs.Any()) break;
                    foreach (var p in procs) { try { p.Kill(); } catch { } }
                    if (wait) foreach (var p in procs) { try { p.WaitForExit(2000); } catch { } }
                    if (i < 2) Thread.Sleep(500); 
                }
            } catch { }
        }
        public static bool FileOrDirectoryExists(string p) => (Directory.Exists(p) || File.Exists(p));
        public static bool CopyFile(string s, string d, bool ovr = true)
        {
            if (!File.Exists(s)) return false;
            try { Directory.CreateDirectory(Path.GetDirectoryName(d)); File.Copy(s, d, ovr); return true; } catch { return false; }
        }
        public static bool DeleteFile(string p, bool err = false) { try { if (File.Exists(p)) File.Delete(p); return true; } catch { if (err) throw; return false; } }
        public static async Task<T> RetryAsync<T>(Func<Task<T>> a, int max = 5, int del = 500, bool th = false)
        {
            for (int i = 0; i < max; i++) { try { return await a(); } catch { if (i == max - 1 && th) throw; await Task.Delay(del * (i + 1)); } }
            return default(T);
        }
        public static async Task RetryActionAsync(Func<Task> a, int max = 5, int del = 500, bool th = false)
        {
            for (int i = 0; i < max; i++) { try { await a(); return; } catch { if (i == max - 1 && th) throw; await Task.Delay(del * (i + 1)); } }
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

                foreach (var dir in Directory.GetDirectories(sLong, "*", SearchOption.AllDirectories))
                {
                    string r = dir.Substring(sLong.Length).TrimStart(Path.DirectorySeparatorChar);
                    if (ex != null && ex.Any(e => r.IndexOf(e, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                    Directory.CreateDirectory(Path.Combine(dLong, r));
                }
                foreach (var f in Directory.GetFiles(sLong, "*.*", SearchOption.AllDirectories))
                {
                    string r = f.Substring(sLong.Length).TrimStart(Path.DirectorySeparatorChar);
                    if (ex != null && ex.Any(e => r.IndexOf(e, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                    string dest = Path.Combine(dLong, r);
                    if (ovr || !File.Exists(dest))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        File.Copy(f, dest, ovr);
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
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return;
                object s = Activator.CreateInstance(t);
                object sc = s.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, s, new object[] { path });
                sc.GetType().InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
                sc.GetType().InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { desc });
                sc.GetType().InvokeMember("Arguments", BindingFlags.SetProperty, null, sc, new object[] { args });
                sc.GetType().InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(target) });
                if (!string.IsNullOrEmpty(icon)) sc.GetType().InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { icon });
                sc.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
            } catch { }
        }
    }
}
