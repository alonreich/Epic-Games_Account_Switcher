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
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Runtime.Versioning;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Writers;
using System.Drawing;

namespace Epic_Games_Account_Switcher_Globals
{
    public partial class Globals
    {
        public static bool VerboseMode;
        public static readonly string Version = "2026.03.30";

        public static string AppDataFolder => AppContext.BaseDirectory;
        public static string UserDataFolder
        {
            get
            {
                
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Epic-Games_Account_Switcher");
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                return path;
            }
        }

        public static bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        public static string GetLogPath()
        {
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule.FileName;
                var installDir = Path.GetDirectoryName(exePath);
                var logName = Path.GetFileNameWithoutExtension(exePath) + ".log";
                return Path.Combine(installDir, logName);
            }
            catch
            {
                return Path.Combine(UserDataFolder, "Epic-Games_Account_Switcher.log");
            }
        }

        public static void WriteToLog(string msg, Exception ex = null)
        {
            try 
            {
                string logPath = GetLogPath();
                FileInfo fi = new FileInfo(logPath);
                
                if (fi.Exists && fi.Length > 5 * 1024 * 1024)
                {
                    // "new data deletes old data" - Keep the last 1MB of logs to maintain context
                    try
                    {
                        byte[] buffer = new byte[1024 * 1024]; // 1MB
                        using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            fs.Seek(-buffer.Length, SeekOrigin.End);
                            fs.Read(buffer, 0, buffer.Length);
                        }
                        string tail = Encoding.UTF8.GetString(buffer);
                        int firstNewline = tail.IndexOf('\n');
                        if (firstNewline != -1) tail = tail.Substring(firstNewline + 1);

                        File.WriteAllText(logPath, $"--- Log Rotated at {DateTime.Now} (Size > 5MB) ---\n" + tail);
                    }
                    catch
                    {
                        File.WriteAllText(logPath, $"--- Log reset at {DateTime.Now} due to size limit (5MB) ---\n");
                    }
                }

                string content = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}" + (ex != null ? $"\n[EXCEPTION] {ex.Message}\nStack: {ex.StackTrace}" : "") + "\n";
                File.AppendAllText(logPath, content);
                
                if (VerboseMode) Console.WriteLine($"[LOG] {msg}");
            } 
            catch { }
        }

        public static void WriteToLog(string msg) => WriteToLog(msg, null);

        public static void DebugWriteLine(string msg)
        {
            if (VerboseMode) WriteToLog("[DEBUG] " + msg);
        }

        public static void ForceDeleteDirectory(string path)
        {
            if (!Directory.Exists(path)) return;
            WriteToLog($"[IO] Force Deleting Directory: {path}");

            for (int i = 0; i < 5; i++)
            {
                try
                {
                    var directory = new DirectoryInfo(path) { Attributes = FileAttributes.Normal };
                    foreach (var info in directory.GetFileSystemInfos("*", SearchOption.AllDirectories))
                    {
                        info.Attributes = FileAttributes.Normal;
                    }
                    directory.Delete(true);
                    return;
                }
                catch (IOException)
                {
                    if (i == 4) throw;
                    Thread.Sleep(500 * (i + 1));
                }
                catch (UnauthorizedAccessException)
                {
                    if (i == 4) throw;
                    Thread.Sleep(500 * (i + 1));
                }
            }
        }

        public static void CreateDataFolder(bool overwrite)
        {
            if (AppDataFolder == UserDataFolder) return;
            if (InstalledToProgramFiles() && !IsAdministrator) return;
            var settingsPath = Path.Combine(UserDataFolder, "appsettings.json");
            if (!File.Exists(settingsPath))
            {
                var sourceSettings = Path.Combine(AppDataFolder, "appsettings.json");
                if (File.Exists(sourceSettings)) CopyFile(sourceSettings, settingsPath, false);
            }
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

        public static bool IsProcessRunning(string name)
        {
            try { return Process.GetProcessesByName(name).Any(p => p.Id != Environment.ProcessId); }
            catch { return false; }
        }

        public static void KillProcess(string name, bool wait = true) 
        { 
            try 
            {
                var currentId = Environment.ProcessId;
                
                for (int i = 0; i < 3; i++)
                {
                    var processes = Process.GetProcessesByName(name).Where(p => p.Id != currentId).ToList();
                    if (!processes.Any()) break;

                    foreach (var p in processes) 
                    {
                        try 
                        {
                            WriteToLog($"[PROC] Terminating process: {p.ProcessName} (PID: {p.Id})");
                            p.Kill(true); 
                        } catch { } 
                    }

                    if (wait)
                    {
                        foreach (var p in processes)
                        {
                            try { p.WaitForExit(2000); } catch { }
                        }
                    }
                    
                    if (i < 2) Thread.Sleep(500); 
                }
            }
            catch (Exception ex) { WriteToLog($"Failed to kill process {name}", ex); }
        }

        public static bool FileOrDirectoryExists(string p) => (Directory.Exists(p) || File.Exists(p));

        public static bool CopyFile(string source, string dest, bool overwrite = true)
        {
            if (!File.Exists(source)) return false;
            try { Directory.CreateDirectory(Path.GetDirectoryName(dest)); File.Copy(source, dest, overwrite); return true; }
            catch { return false; }
        }

        public static bool DeleteFile(string path, bool throwErr = false) { try { if (File.Exists(path)) File.Delete(path); return true; } catch (IOException) { return false; } catch (Exception) { if (throwErr) throw; return false; } }

        public static async Task<T> RetryAsync<T>(Func<Task<T>> action, int maxRetries = 5, int delayMs = 500, bool throwOnFailure = false)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                try { return await action(); }
                catch (IOException) when (i < maxRetries - 1) { await Task.Delay(delayMs * (i + 1)); }
                catch (UnauthorizedAccessException) when (i < maxRetries - 1) { await Task.Delay(delayMs * (i + 1)); }
            }
            if (throwOnFailure) return await action();
            try { return await action(); } catch { return default; }
        }

        public static async Task RetryActionAsync(Func<Task> action, int maxRetries = 5, int delayMs = 500, bool throwOnFailure = false)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                try { await action(); return; }
                catch (IOException) when (i < maxRetries - 1) { await Task.Delay(delayMs * (i + 1)); }
                catch (UnauthorizedAccessException) when (i < maxRetries - 1) { await Task.Delay(delayMs * (i + 1)); }
            }
            if (throwOnFailure) await action();
            else try { await action(); } catch { }
        }

        public static string ReadAllText(string f) { using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); using var tr = new StreamReader(fs); return tr.ReadToEnd(); }

        public static async Task<bool> CopyFilesRecursiveAsync(string inputFolder, string outputFolder, bool overwrite = true, List<string> excludeFolders = null)
        {
            return await Task.Run(() =>
            {
                try
                {
                    WriteToLog($"[IO] Recursive Copy (Async): {inputFolder} -> {outputFolder}");
                    inputFolder = Path.GetFullPath(inputFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    outputFolder = Path.GetFullPath(outputFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    Directory.CreateDirectory(outputFolder);

                    var allDirs = Directory.GetDirectories(inputFolder, "*", SearchOption.AllDirectories);
                    foreach (var d in allDirs)
                    {
                        string rd = d.Substring(inputFolder.Length);
                        if (excludeFolders != null && excludeFolders.Any(ex => rd.Contains(ex, StringComparison.OrdinalIgnoreCase))) continue;
                        Directory.CreateDirectory(Path.Combine(outputFolder, rd));
                    }

                    var allFiles = Directory.GetFiles(inputFolder, "*.*", SearchOption.AllDirectories);
                    foreach (var f in allFiles)
                    {
                        string rd = f.Substring(inputFolder.Length);
                        if (excludeFolders != null && excludeFolders.Any(ex => rd.Contains(ex, StringComparison.OrdinalIgnoreCase))) continue;
                        string df = Path.Combine(outputFolder, rd);
                        if (overwrite || !File.Exists(df))
                        {
                            File.Copy(f, df, overwrite);
                        }
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    WriteToLog($"[ERROR] Recursive copy (Async) failed: {inputFolder}", ex);
                    throw;
                }
            });
        }

        public static bool CopyFilesRecursive(string inputFolder, string outputFolder, bool overwrite = true, List<string> excludeFolders = null)
        {
            try {
                WriteToLog($"[IO] Recursive Copy: {inputFolder} -> {outputFolder}");
                inputFolder = Path.GetFullPath(inputFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                outputFolder = Path.GetFullPath(outputFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                Directory.CreateDirectory(outputFolder);
                
                foreach (var d in Directory.GetDirectories(inputFolder, "*", SearchOption.AllDirectories)) 
                { 
                    string rd = d.Substring(inputFolder.Length);
                    if (excludeFolders != null && excludeFolders.Any(ex => rd.Contains(ex, StringComparison.OrdinalIgnoreCase))) continue;
                    Directory.CreateDirectory(Path.Combine(outputFolder, rd)); 
                }
                
                foreach (var f in Directory.GetFiles(inputFolder, "*.*", SearchOption.AllDirectories)) 
                { 
                    string rd = f.Substring(inputFolder.Length);
                    if (excludeFolders != null && excludeFolders.Any(ex => rd.Contains(ex, StringComparison.OrdinalIgnoreCase))) continue;
                    string df = Path.Combine(outputFolder, rd); 
                    if (overwrite || !File.Exists(df)) File.Copy(f, df, overwrite); 
                }
                return true;
            } catch (Exception ex) { 
                WriteToLog($"[ERROR] Recursive copy failed: {inputFolder}", ex);
                throw;
            }
        }

        public static void CreateShortcut(string targetPath, string shortcutPath, string description, string args = "", string iconPath = "")
        {
            try {
                var folder = Path.GetDirectoryName(shortcutPath);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return;
                dynamic shell = Activator.CreateInstance(t);
                var shortcut = shell.CreateShortcut(shortcutPath);
                shortcut.TargetPath = targetPath;
                shortcut.Description = description;
                shortcut.Arguments = args;
                shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(iconPath)) shortcut.IconLocation = iconPath;
                shortcut.Save();
            } catch (Exception ex) { 
                WriteToLog("Shortcut creation failed for: " + shortcutPath, ex);
            }
        }
    }
}
