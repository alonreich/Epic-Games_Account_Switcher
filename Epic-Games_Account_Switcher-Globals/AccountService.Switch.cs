using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Epic_Games_Account_Switcher_Globals
{
    /// <summary>Epic process control, safe file moves, session capture and account switching.</summary>
    public partial class AccountService
    {
        private static void SetLastSwitchFailure(string message)
        {
            _lastSwitchFailureMessage = string.IsNullOrWhiteSpace(message) ? "" : message.Trim();
        }

        private static void LogSwitch(string operationId, string message, Exception ex = null)
        {
            Globals.WriteToLog("[SWITCH:" + operationId + "] " + message, ex);
        }

        private static string ValidationSummary(SessionValidationResult result)
        {
            if (result == null) return "NULL";
            return "Valid=" + result.IsValid
                + "; Account=" + Globals.SafeLogId(result.AccountId)
                + "; ExpiresUtc=" + (result.ExpiresUtc.HasValue ? result.ExpiresUtc.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z" : "NULL")
                + "; Message=" + (string.IsNullOrWhiteSpace(result.Message) ? "OK" : result.Message);
        }

        private static string PathStateSummary(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return "NULL";
                if (File.Exists(path))
                {
                    var fi = new FileInfo(path);
                    return "file bytes=" + fi.Length + "; updatedUtc=" + fi.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z";
                }
                if (!Directory.Exists(path)) return "missing";

                long bytes = 0;
                var count = 0;
                var capped = false;
                DateTime newest = Directory.GetLastWriteTimeUtc(path);
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    var fi = new FileInfo(file);
                    bytes += fi.Length;
                    count++;
                    if (fi.LastWriteTimeUtc > newest) newest = fi.LastWriteTimeUtc;
                    if (count >= 5000)
                    {
                        capped = true;
                        break;
                    }
                }
                return "dir files=" + count + (capped ? "+" : "") + "; bytes=" + bytes + (capped ? "+" : "") + "; updatedUtc=" + newest.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z";
            }
            catch (Exception ex)
            {
                return "unknown: " + ex.Message;
            }
        }

        private static string ProcessSummary(IEnumerable<Process> processes)
        {
            if (processes == null) return "none";
            var parts = new List<string>();
            foreach (var p in processes)
            {
                try { parts.Add(p.ProcessName + "(" + p.Id + ")"); }
                catch { }
            }
            return parts.Count == 0 ? "none" : string.Join(", ", parts);
        }

        private static List<string> GetStateRoots()
        {
            var roots = new List<string> { EpicSavedPath, Path.Combine(RoamingAppData, "Epic Games", "EpicGamesLauncher") };
            if (Directory.Exists(ProgramDataManifestsPath)) roots.Add(ProgramDataManifestsPath);
            foreach (var rel in EOSFoldersToCapture)
            {
                roots.Add(Path.Combine(LocalAppData, "Epic Games", rel));
                roots.Add(Path.Combine(LocalAppData, rel));
            }
            return roots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<string> GetBlockedStateRoots()
        {
            var blocked = new List<string>();
            foreach (var root in GetStateRoots())
            {
                if (!Directory.Exists(root)) continue;
                if (!ProbeDirectoryWritable(root)) blocked.Add(root);
            }
            return blocked;
        }

        private static bool WaitForProcessRunning(string processName, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(0, timeoutMs));
            while (DateTime.UtcNow <= deadline)
            {
                if (Globals.IsProcessRunning(processName)) return true;
                Thread.Sleep(250);
            }
            return Globals.IsProcessRunning(processName);
        }

        private static async Task<bool> EnsureEpicIsClosed(int timeoutMs = 60000, CancellationToken cancellationToken = default(CancellationToken))
        {
            var sw = Stopwatch.StartNew();
            var epicPaths = GetEpicInstallPaths();
            var lastWritableLogAt = -10000L;
            var loggedPids = new HashSet<int>();
            var sysTaskkill = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe");
            var sysPowershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");

            Globals.WriteToLog("[KILL] Starting Epic process cleanup. KnownInstallRoots=" + epicPaths.Count);
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var active = GetEpicProcesses(epicPaths);
                if (active.Count == 0)
                {
                    if (VerifyWritableStateRoots())
                    {
                        Globals.WriteToLog("[KILL] Epic cleanup complete. ElapsedMs=" + sw.ElapsedMilliseconds);
                        return true;
                    }
                    if (sw.ElapsedMilliseconds - lastWritableLogAt >= 5000)
                    {
                        lastWritableLogAt = sw.ElapsedMilliseconds;
                        Globals.WriteToLog("[KILL] Waiting for writable Epic state roots. BlockedRoots=" + string.Join(" | ", GetBlockedStateRoots()));
                    }
                    await Task.Delay(100, cancellationToken);
                    continue;
                }

                foreach (var p in active)
                {
                    try
                    {
                        var pid = p.Id;
                        var name = p.ProcessName;
                        if (!loggedPids.Contains(pid))
                        {
                            loggedPids.Add(pid);
                            Globals.WriteToLog("[KILL] Terminating process: " + name + " (" + pid + ")");
                        }
                        try { p.Kill(); } catch { }
                        try { Process.Start(new ProcessStartInfo(sysTaskkill, "/F /T /PID " + pid) { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit(1000); } catch { }
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }

                var targetImages = new[] { "EpicGamesLauncher.exe", "EpicWebHelper.exe", "EpicOnlineServices.exe", "EpicOnlineServicesHost.exe", "EpicOnlineServicesUserHelper.exe", "EOSUserHelper.exe", "EOSOverlay.exe", "CrashReportClient.exe", "UnrealCEFSubProcess.exe" };
                foreach (var img in targetImages)
                {
                    try { Process.Start(new ProcessStartInfo(sysTaskkill, "/F /IM " + img) { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit(500); } catch { }
                }

                try
                {
                    if (File.Exists(sysPowershell))
                    {
                        Process.Start(new ProcessStartInfo(sysPowershell, "-NoProfile -ExecutionPolicy Bypass -Command \"Stop-Process -Name 'EpicGamesLauncher','EpicWebHelper','EpicOnlineServices','EpicOnlineServicesHost','EpicOnlineServicesUserHelper','EOSUserHelper','EOSOverlay','CrashReportClient','UnrealCEFSubProcess' -Force -ErrorAction SilentlyContinue\"") { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit(1000);
                    }
                }
                catch { }

                var pollSw = Stopwatch.StartNew();
                while (pollSw.ElapsedMilliseconds < 1000)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var check = GetEpicProcesses(epicPaths);
                    try { if (check.Count == 0 && VerifyWritableStateRoots()) break; }
                    finally { foreach (var p in check) try { p.Dispose(); } catch { } }
                    await Task.Delay(50, cancellationToken);
                }
            }

            var remaining = GetEpicProcesses(epicPaths);
            bool safeToProceed = false;
            try
            {
                bool mainLauncherAlive = remaining.Any(p => p.ProcessName.Equals("EpicGamesLauncher", StringComparison.OrdinalIgnoreCase));
                bool rootsWritable = VerifyWritableStateRoots();
                safeToProceed = !mainLauncherAlive && rootsWritable;
                Globals.WriteToLog("[KILL] Timed out closing Epic. ElapsedMs=" + sw.ElapsedMilliseconds + "; Remaining=" + ProcessSummary(remaining) + "; BlockedRoots=" + string.Join(" | ", GetBlockedStateRoots()) + "; SafeToProceed=" + safeToProceed);
            }
            finally
            {
                foreach (var p in remaining) try { p.Dispose(); } catch { }
            }
            return safeToProceed;
        }

        private static List<string> GetEpicInstallPaths()
        {
            var result = new List<string>();
            try
            {
                foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
                {
                    foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                    {
                        try
                        {
                            using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
                            using (var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\EpicGamesLauncher"))
                            {
                                if (key == null) continue;
                                AddEpicInstallPathCandidate(result, key.GetValue("InstallLocation") as string);
                                AddEpicInstallPathCandidate(result, key.GetValue("DisplayIcon") as string);
                                AddEpicInstallPathCandidate(result, key.GetValue("UninstallString") as string);
                            }
                        }
                        catch { }
                    }
                }

                AddEpicInstallPathCandidate(result, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Epic Games", "Launcher"));
                AddEpicInstallPathCandidate(result, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic Games", "Launcher"));
            }
            catch { }

            var cleaned = new List<string>();
            foreach (var p in result)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                try
                {
                    var fp = Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
                    if (!cleaned.Contains(fp, StringComparer.OrdinalIgnoreCase))
                        cleaned.Add(fp);
                }
                catch { }
            }
            return cleaned;
        }

        private static void AddEpicInstallPathCandidate(List<string> result, string value)
        {
            if (result == null || string.IsNullOrWhiteSpace(value)) return;
            try
            {
                var text = value.Trim().Trim('"');
                var exeMatch = Regex.Match(text, @"[A-Za-z]:\\[^""]*EpicGamesLauncher\.exe", RegexOptions.IgnoreCase);
                if (exeMatch.Success) text = exeMatch.Value;

                string root = null;
                if (File.Exists(text)) root = Path.GetDirectoryName(text);
                else if (Directory.Exists(text)) root = text;

                if (string.IsNullOrWhiteSpace(root)) return;

                var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var launcherIndex = full.LastIndexOf(Path.DirectorySeparatorChar + "Launcher", StringComparison.OrdinalIgnoreCase);
                if (launcherIndex >= 0) full = full.Substring(0, launcherIndex + "\\Launcher".Length);
                result.Add(full);
            }
            catch { }
        }

        private static List<Process> GetEpicProcesses(List<string> epicPaths)
        {
            var launcherProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "EpicGamesLauncher",
                "EpicWebHelper",
                "EpicOnlineServices",
                "EpicOnlineServicesUserHelper",
                "EOSUserHelper",
                "EOSOverlay"
            };

            var active = new List<Process>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var n = p.ProcessName;
                    var path = "";
                    try { if (p.MainModule != null) path = p.MainModule.FileName; } catch { }
                    var lower = string.IsNullOrWhiteSpace(path) ? "" : Path.GetFullPath(path).ToLowerInvariant();

                    if (launcherProcessNames.Contains(n))
                    {
                        active.Add(p);
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(lower) && epicPaths.Any(ep => IsPathUnder(lower, ep))) active.Add(p);
                    else p.Dispose();
                }
                catch { try { p.Dispose(); } catch { } }
            }
            return active;
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

        private static bool VerifyWritableStateRoots()
        {
            var ok = true;
            foreach (var root in GetStateRoots())
            {
                if (!Directory.Exists(root)) continue;
                ok = ok && ProbeDirectoryWritable(root);
            }
            return ok;
        }

        private static bool ProbeDirectoryWritable(string root)
        {
            try
            {
                Directory.CreateDirectory(root);
                var probe = Path.Combine(root, ".epic_switcher_probe_" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "probe");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        private static async Task<bool> SafeDeleteDirectoryAsync(string path, int retries = 3, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return true;
            try
            {
                await Task.Run(() => Globals.ForceDeleteDirectory(path), cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            return !Directory.Exists(path);
        }

        private static async Task<bool> SafeReplaceDirectoryAsync(string source, string target, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!Directory.Exists(source)) return true;
            var tmp = target + "_t" + GetShortId();
            await SafeDeleteDirectoryAsync(tmp, 10, cancellationToken);
            await CopyDirectoryStrictAsync(source, tmp, cancellationToken);
            await SafeDeleteDirectoryAsync(target, 10, cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            await SafeMovePathAsync(tmp, target, cancellationToken);
            return true;
        }

        private static async Task SafeReplaceFileAsync(string source, string target, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!File.Exists(source)) return;
            await Globals.RetryActionAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                var tmp = target + ".t" + GetShortId();
                File.Copy(source, tmp, true);
                AtomicReplaceFile(tmp, target);
                return Task.FromResult(0);
            }, 5, 500, true, cancellationToken);
        }

        private static async Task CopyDirectoryStrictAsync(string source, string dest, CancellationToken cancellationToken = default(CancellationToken))
        {
            var success = await Globals.RetryAsync(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Browser cache is rebuilt by Epic on its own, so saved profiles never carry it (keeps them small and switches fast).
                return await Globals.CopyFilesRecursiveAsync(source, dest, true, CacheFilter.IsDisposable);
            }, 5, 1000, true, cancellationToken);
            if (!success) throw new IOException("Recursive copy failed from " + source + " to " + dest + ". [MAX_PATH? LOCK?]");
        }

        private static async Task CaptureSessionToFolder(string source, string dest, CancellationToken cancellationToken = default(CancellationToken))
        {
            await SafeDeleteDirectoryAsync(dest, 10, cancellationToken);
            Directory.CreateDirectory(dest);
            foreach (var f in AuthFiles.Concat(AuthBinaryFiles))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var s = Path.Combine(source, f);
                if (File.Exists(s)) await SafeReplaceFileAsync(s, Path.Combine(dest, f), cancellationToken);
            }
            foreach (var item in SessionItemsToCapture)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sPath = Path.Combine(source, item);
                var dPath = Path.Combine(dest, item);
                if (Directory.Exists(sPath)) await CopyDirectoryStrictAsync(sPath, dPath, cancellationToken);
                else if (File.Exists(sPath)) await SafeReplaceFileAsync(sPath, dPath, cancellationToken);
            }
            var eosDest = Path.Combine(dest, "EOS_Data");
            foreach (var rel in EOSFoldersToCapture)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sPath = Path.Combine(LocalAppData, "Epic Games", rel);
                var isRoot = false;
                if (!Directory.Exists(sPath)) { sPath = Path.Combine(LocalAppData, rel); isRoot = true; }
                if (Directory.Exists(sPath))
                {
                    var dPath = Path.Combine(eosDest, rel);
                    await CopyDirectoryStrictAsync(sPath, dPath, cancellationToken);
                    if (isRoot) File.WriteAllText(dPath + ".is_root", "");
                }
            }
            var rDest = Path.Combine(dest, "Roaming_Data");
            var rSrc = Path.Combine(RoamingAppData, "Epic Games", "EpicGamesLauncher");
            if (Directory.Exists(rSrc)) await CopyDirectoryStrictAsync(rSrc, Path.Combine(rDest, "EpicGamesLauncher"), cancellationToken);
            if (Directory.Exists(ProgramDataManifestsPath)) await CopyDirectoryStrictAsync(ProgramDataManifestsPath, Path.Combine(dest, "ProgramData_Data", "Manifests"), cancellationToken);
        }

        private static async Task SafeMovePathAsync(string source, string target, CancellationToken cancellationToken = default(CancellationToken))
        {
            await Globals.RetryActionAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Directory.Exists(source) && !File.Exists(source)) return Task.FromResult(0);

                var parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);

                if (File.Exists(target)) try { File.Delete(target); } catch { }
                if (Directory.Exists(target))
                {
                    try { Globals.ForceDeleteDirectory(target); } catch { }
                    if (Directory.Exists(target))
                    {
                        try { Directory.Move(target, target + "_old_" + Guid.NewGuid().ToString("N").Substring(0, 6)); } catch { }
                    }
                }

                var srcRoot = Path.GetPathRoot(Path.GetFullPath(source));
                var dstRoot = Path.GetPathRoot(Path.GetFullPath(target));
                var isCrossVolume = !string.Equals(srcRoot, dstRoot, StringComparison.OrdinalIgnoreCase);

                if (Directory.Exists(source))
                {
                    if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Move target already exists: " + target);
                    if (isCrossVolume)
                    {
                        Globals.CopyFilesRecursive(source, target, true);
                        Globals.ForceDeleteDirectory(source);
                    }
                    else
                    {
                        try { Directory.Move(source, target); }
                        catch (IOException) { Globals.CopyFilesRecursive(source, target, true); Globals.ForceDeleteDirectory(source); }
                    }
                }
                else
                {
                    if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Move target already exists: " + target);
                    if (isCrossVolume)
                    {
                        File.Copy(source, target, true);
                        File.Delete(source);
                    }
                    else
                    {
                        try { File.Move(source, target); }
                        catch (IOException) { File.Copy(source, target, true); File.Delete(source); }
                    }
                }

                return Task.FromResult(0);
            }, 10, 500, true, cancellationToken);
        }

        private static void SafeMovePathBlocking(string source, string target)
        {
            for (var i = 0; i < 10; i++)
            {
                try
                {
                    if (!Directory.Exists(source) && !File.Exists(source)) return;

                    var parent = Path.GetDirectoryName(target);
                    if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);

                    if (File.Exists(target)) try { File.Delete(target); } catch { }
                    if (Directory.Exists(target))
                    {
                        try { Globals.ForceDeleteDirectory(target); } catch { }
                        if (Directory.Exists(target))
                        {
                            try { Directory.Move(target, target + "_old_" + Guid.NewGuid().ToString("N").Substring(0, 6)); } catch { }
                        }
                    }

                    var srcRoot = Path.GetPathRoot(Path.GetFullPath(source));
                    var dstRoot = Path.GetPathRoot(Path.GetFullPath(target));
                    var isCrossVolume = !string.Equals(srcRoot, dstRoot, StringComparison.OrdinalIgnoreCase);

                    if (Directory.Exists(source))
                    {
                        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Move target already exists: " + target);
                        if (isCrossVolume)
                        {
                            Globals.CopyFilesRecursive(source, target, true);
                            Globals.ForceDeleteDirectory(source);
                        }
                        else
                        {
                            try { Directory.Move(source, target); }
                            catch (IOException) { Globals.CopyFilesRecursive(source, target, true); Globals.ForceDeleteDirectory(source); }
                        }
                    }
                    else
                    {
                        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Move target already exists: " + target);
                        if (isCrossVolume)
                        {
                            File.Copy(source, target, true);
                            File.Delete(source);
                        }
                        else
                        {
                            try { File.Move(source, target); }
                            catch (IOException) { File.Copy(source, target, true); File.Delete(source); }
                        }
                    }

                    return;
                }
                catch
                {
                    if (i == 9) throw;
                    Thread.Sleep(500 * (i + 1));
                }
            }
        }

        public static void CleanOrphanedTempFolders()
        {
            try
            {
                if (!Directory.Exists(BaseDataPath)) return;
                var now = DateTime.UtcNow;
                foreach (var dir in Directory.GetDirectories(BaseDataPath))
                {
                    var name = Path.GetFileName(dir);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var isTemp = name.StartsWith("_tmp_res", StringComparison.OrdinalIgnoreCase) ||
                                 name.Equals("_switch_rb", StringComparison.OrdinalIgnoreCase) ||
                                 name.IndexOf("_rollback_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 name.IndexOf("_n", StringComparison.Ordinal) > 3 ||
                                 name.IndexOf("_r", StringComparison.Ordinal) > 3;
                    if (isTemp)
                    {
                        try
                        {
                            var info = new DirectoryInfo(dir);
                            if (info.LastWriteTimeUtc < now.AddHours(-1) || info.CreationTimeUtc < now.AddHours(-1))
                            {
                                Globals.ForceDeleteDirectory(dir);
                                Globals.WriteToLog("[CLEANUP] Purged orphaned temp directory: " + name);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("[CLEANUP] Failed to clean orphaned temp folders", ex);
            }
        }

        private static async Task ReplacePathWithRollbackAsync(string source, string target, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!Directory.Exists(source) && !File.Exists(source)) return;

            var rollback = target + "_rollback_" + GetShortId();
            var rollbackPrepared = false;
            try
            {
                if (Directory.Exists(rollback)) await SafeDeleteDirectoryAsync(rollback, 10, cancellationToken);
                if (File.Exists(rollback)) File.Delete(rollback);

                if (Directory.Exists(target) || File.Exists(target))
                {
                    await SafeMovePathAsync(target, rollback, cancellationToken);
                    rollbackPrepared = true;
                }

                await SafeMovePathAsync(source, target, cancellationToken);

                if (rollbackPrepared)
                {
                    try
                    {
                        if (Directory.Exists(rollback)) await SafeDeleteDirectoryAsync(rollback, 3, cancellationToken);
                        else if (File.Exists(rollback)) File.Delete(rollback);
                    }
                    catch { }
                }
            }
            catch
            {
                try
                {
                    if (!Directory.Exists(target) && !File.Exists(target) && rollbackPrepared && (Directory.Exists(rollback) || File.Exists(rollback)))
                    {
                        await SafeMovePathAsync(rollback, target, CancellationToken.None);
                    }
                }
                catch (Exception rollbackEx)
                {
                    Globals.WriteToLog("[IO] Failed to restore rollback after replacement failure. Target=" + target, rollbackEx);
                }
                throw;
            }
        }

        public static async Task<Tuple<bool, string>> CaptureCurrentAccount(string nameOverride = null, string emailOverride = null, string imagePathOverride = null, string descriptionOverride = null, string passwordOverride = null, Action<string> progress = null, Func<string, Task<bool>> confirmOverwrite = null, bool forceOverwrite = false, CancellationToken cancellationToken = default(CancellationToken), string expectedAccountId = null)
        {
            await SessionOperationLock.WaitAsync(cancellationToken);
            string tmp = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                _lastDetected = new Tuple<string, string, string>(null, "N/A", "N/A");
                var det = await GetDetectedMetadataAsync();
                if (string.IsNullOrWhiteSpace(det.Item1)) return new Tuple<bool, string>(false, L.T("No account is signed in to Epic Games Launcher. Sign in there first, then try again."));
                if (!string.IsNullOrWhiteSpace(expectedAccountId) && !string.Equals(det.Item1, expectedAccountId, StringComparison.OrdinalIgnoreCase))
                {
                    Globals.WriteToLog("[CAPTURE] Signed-in account does not match. Expected " + Globals.SafeLogId(expectedAccountId) + " but detected " + Globals.SafeLogId(det.Item1) + ".");
                    return new Tuple<bool, string>(false, L.T("A different account is signed in to Epic Games Launcher. Sign in to the account you want to save, then try again."));
                }
                // Ask before anything is closed or changed, so Cancel leaves Epic Games Launcher running. (Item2 null = cancelled, no error.)
                var accounts = GetAccounts();
                var existing = accounts.FirstOrDefault(a => a.Id == det.Item1);
                if (existing != null && !forceOverwrite)
                {
                    if (confirmOverwrite == null || !await confirmOverwrite(existing.Name)) return new Tuple<bool, string>(false, null);
                }
                progress?.Invoke(L.T("Closing Epic Games..."));
                if (!await EnsureEpicIsClosed(60000, cancellationToken)) return new Tuple<bool, string>(false, L.T("Epic Games Launcher could not be closed. Close it yourself (also from the notification area), then try again."));
                var liveValidation = ValidateSession(EpicSavedPath, det.Item1);
                if (!liveValidation.IsValid)
                {
                    Globals.WriteToLog("[CAPTURE] Live session invalid: " + liveValidation.Message);
                    return new Tuple<bool, string>(false, L.T("Epic Games Launcher's sign-in looks incomplete or expired. Open Epic Games Launcher, sign in again (tick \"Remember me\"), then try again."));
                }
                var acc = existing ?? new Account(det.Item1, nameOverride ?? (det.Item2 == "N/A" ? "Epic Account" : det.Item2));
                if (!string.IsNullOrWhiteSpace(nameOverride)) acc.Name = nameOverride.Trim();
                else if (!string.IsNullOrWhiteSpace(det.Item2) && det.Item2 != "N/A") acc.Name = det.Item2;
                if (emailOverride != null) acc.Email = emailOverride.Trim();
                else if (!string.IsNullOrWhiteSpace(det.Item3) && det.Item3 != "N/A") acc.Email = det.Item3;
                if (descriptionOverride != null) acc.Description = descriptionOverride;
                if (passwordOverride != null) acc.Password = passwordOverride;
                if (!string.IsNullOrWhiteSpace(imagePathOverride)) acc.ProfileImagePath = imagePathOverride;
                acc.ProfileImagePath = NormalizeProfileImagePath(acc.ProfileImagePath);
                acc.AccentColor = GetDeterministicColor(acc.Name ?? acc.Id);
                StampAccountUsage(acc, true);
                progress?.Invoke(L.T("Capturing session..."));
                var target = Path.Combine(BaseDataPath, acc.Id);
                tmp = target + "_n" + GetShortId();
                await CaptureSessionToFolder(EpicSavedPath, tmp, cancellationToken);
                var capturedValidation = ValidateSession(tmp, acc.Id);
                if (!capturedValidation.IsValid)
                {
                    Globals.WriteToLog("[CAPTURE] Captured session failed verification: " + capturedValidation.Message);
                    await SafeDeleteDirectoryAsync(tmp, 10, cancellationToken);
                    return new Tuple<bool, string>(false, L.T("The sign-in could not be copied completely. Open Epic Games Launcher, make sure you are signed in, then try again."));
                }
                acc.RegistryData = CaptureRegistryData();
                acc.Credentials = CredentialService.CaptureCredentials(acc.Id);
                acc.RememberMeDataByPath = GetRememberMeDataByPath();
                acc.RememberMeData = acc.RememberMeDataByPath.Values.FirstOrDefault();
                acc.LauncherWindowSettingsByPath = GetLauncherWindowSettingsByPath();
                acc.LauncherWindowSettings = acc.LauncherWindowSettingsByPath.Values.FirstOrDefault();
                await ReplacePathWithRollbackAsync(tmp, target, cancellationToken);
                if (existing == null) accounts.Add(acc);
                SaveAccounts(accounts);
                return new Tuple<bool, string>(true, null);
            }
            catch (OperationCanceledException)
            {
                if (!string.IsNullOrWhiteSpace(tmp) && Directory.Exists(tmp)) await SafeDeleteDirectoryAsync(tmp);
                throw;
            }
            catch (Exception ex) { Globals.WriteToLog("[CAPTURE] Fatal error", ex); return new Tuple<bool, string>(false, L.T("Something unexpected stopped the account from being saved. Try again. If it keeps happening, use SHOW LOGS for details.")); }
            finally { SessionOperationLock.Release(); }
        }

        private static async Task RefreshSnapshotOfCurrentSavedAccount(List<Account> accounts, string switchingToAccountId, string currentAccountIdHint, Action<string> progress, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var curId = string.IsNullOrWhiteSpace(currentAccountIdHint) ? GetCurrentAccountId() : currentAccountIdHint;
            if (string.IsNullOrWhiteSpace(curId)) curId = accounts.FirstOrDefault(a => a.IsActive)?.Id;
            if (string.IsNullOrWhiteSpace(curId) || string.Equals(curId, switchingToAccountId, StringComparison.OrdinalIgnoreCase)) return;
            var acc = accounts.FirstOrDefault(a => string.Equals(a.Id, curId, StringComparison.OrdinalIgnoreCase));
            if (acc == null) return;
            var liveValidation = ValidateSession(EpicSavedPath, curId);
            if (!liveValidation.IsValid) return;
            progress?.Invoke(L.T("Saving current session..."));
            acc.RegistryData = CaptureRegistryData();
            acc.Credentials = CredentialService.CaptureCredentials(acc.Id);
            acc.RememberMeDataByPath = GetRememberMeDataByPath();
            acc.RememberMeData = acc.RememberMeDataByPath.Values.FirstOrDefault();
            acc.LauncherWindowSettingsByPath = GetLauncherWindowSettingsByPath();
            acc.LauncherWindowSettings = acc.LauncherWindowSettingsByPath.Values.FirstOrDefault();
            StampAccountUsage(acc, false);
            var target = Path.Combine(BaseDataPath, acc.Id);
            var tmp = target + "_r" + GetShortId();
            try
            {
                await CaptureSessionToFolder(EpicSavedPath, tmp, cancellationToken);
                var capturedValidation = ValidateSession(tmp, acc.Id);
                if (!capturedValidation.IsValid) { await SafeDeleteDirectoryAsync(tmp, 10, cancellationToken); return; }
                await ReplacePathWithRollbackAsync(tmp, target, cancellationToken);
                SaveAccounts(accounts);
            }
            catch (OperationCanceledException)
            {
                if (Directory.Exists(tmp)) await SafeDeleteDirectoryAsync(tmp);
                throw;
            }
        }

        private static void MarkActiveAccount(List<Account> accounts, string activeId)
        {
            foreach (var acc in accounts) acc.IsActive = string.Equals(acc.Id, activeId, StringComparison.OrdinalIgnoreCase);
        }

        private static void StampAccountUsage(Account account, bool incrementUsage)
        {
            if (account == null) return;
            account.LastPlayed = DateTime.Now;
            if (incrementUsage) account.UsageCount = Math.Max(0, account.UsageCount) + 1;
        }

        public static async Task<Tuple<bool, string>> CaptureFromExchangeCodeAsync(string exchangeCode, Action<string> progress, CancellationToken cancellationToken = default(CancellationToken))
        {
            await SessionOperationLock.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Invoke(L.T("Closing Epic Games..."));
                if (!await EnsureEpicIsClosed(60000, cancellationToken))
                    return new Tuple<bool, string>(false, L.T("Epic Games Launcher could not be closed. Close it yourself (also from the notification area), then try again."));

                // Purge existing auth state to ensure clean capture
                try
                {
                    foreach (var authFile in AuthFiles)
                    {
                        var path = Path.Combine(EpicSavedPath, authFile);
                        if (File.Exists(path)) File.Delete(path);
                    }
                    foreach (var authFile in AuthBinaryFiles)
                    {
                        var path = Path.Combine(EpicSavedPath, authFile);
                        if (File.Exists(path)) File.Delete(path);
                    }
                    var dataPath = Path.Combine(EpicSavedPath, "Data");
                    if (Directory.Exists(dataPath))
                    {
                        foreach (var dat in Directory.GetFiles(dataPath, "OC_*.dat"))
                            File.Delete(dat);
                    }
                }
                catch (Exception ex)
                {
                    Globals.WriteToLog("[EXCHANGE] Failed to purge existing auth state", ex);
                }

                progress?.Invoke(L.T("Processing login..."));
                var exe = ResolveEpicLauncherExecutable();
                if (string.IsNullOrEmpty(exe))
                    return new Tuple<bool, string>(false, L.T("Epic Games Launcher was not found on this PC. Install it, then try again."));

                var args = $"-exchangecode={exchangeCode} -nosplash -silent -minimized";
                Process.Start(new ProcessStartInfo { FileName = exe, Arguments = args, UseShellExecute = true });

                var deadline = DateTime.UtcNow.AddSeconds(45);
                Tuple<string, string, string> detected = null;
                while (DateTime.UtcNow < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _lastDetected = new Tuple<string, string, string>(null, "N/A", "N/A"); // Force re-detection
                    detected = await GetDetectedMetadataAsync();
                    if (!string.IsNullOrWhiteSpace(detected.Item1))
                        break;
                    await Task.Delay(2000, cancellationToken);
                }

                if (detected == null || string.IsNullOrWhiteSpace(detected.Item1))
                    return new Tuple<bool, string>(false, L.T("Epic Games Launcher did not finish signing in within 45 seconds. Check your internet connection, then try again."));

                // We can't hold the lock when calling CaptureCurrentAccount because it also waits on it.
                // We'll release the lock early in the finally block.
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("[EXCHANGE] Fatal error during exchange capture", ex);
                return new Tuple<bool, string>(false, L.T("Something unexpected stopped the account from being saved. Try again. If it keeps happening, use SHOW LOGS for details."));
            }
            finally
            {
                SessionOperationLock.Release();
            }

            // Now capture the account normally (CaptureCurrentAccount acquires the lock again)
            var captureResult = await CaptureCurrentAccount(null, null, null, null, null, progress, null, true, cancellationToken, null);
            
            // Try to close Epic again so it doesn't linger
            try { await EnsureEpicIsClosed(15000, cancellationToken); } catch { }
            
            return captureResult;
        }

        public static void PrepareLauncherForInitialSync(int maxWaitMs = 8000)
        {
            try
            {
                var waitMs = Math.Max(0, maxWaitMs);
                var beforeId = GetCurrentAccountId();
                var launcherStarted = AutoLaunchEpicLauncher(false);

                if (waitMs == 0 || (!launcherStarted && string.IsNullOrWhiteSpace(beforeId))) return;

                var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
                var shortDeadline = DateTime.UtcNow.AddMilliseconds(Math.Min(waitMs, 3000));
                while (DateTime.UtcNow < deadline)
                {
                    var launcherRunning = Globals.IsProcessRunning("EpicGamesLauncher");
                    var currentId = GetCurrentAccountId();
                    if (launcherRunning && !string.IsNullOrWhiteSpace(currentId)) break;
                    if (launcherRunning && !string.IsNullOrWhiteSpace(beforeId) && DateTime.UtcNow >= shortDeadline) break;
                    Thread.Sleep(500);
                }

                Globals.WriteToLog("[SYNC] Initial launcher sync. Before=" + (string.IsNullOrWhiteSpace(beforeId) ? "NULL" : beforeId) + "; After=" + (GetCurrentAccountId() ?? "NULL"));
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("[SYNC] Initial launcher sync failed", ex);
            }
        }

        public static bool AutoLaunchEpicLauncher(bool minimized = true)
        {
            if (Globals.IsProcessRunning("EpicGamesLauncher"))
            {
                Globals.WriteToLog("[LAUNCHER] Epic Games Launcher already running.");
                return true;
            }
            try
            {
                var exe = ResolveEpicLauncherExecutable();
                if (!string.IsNullOrEmpty(exe))
                {
                    var args = minimized ? "-minimized -silent" : "";
                    Globals.WriteToLog("[LAUNCHER] Starting Epic Games Launcher by executable. Exe=" + exe + "; Args=" + (string.IsNullOrWhiteSpace(args) ? "none" : args));
                    var started = Process.Start(new ProcessStartInfo { FileName = exe, Arguments = args, UseShellExecute = true });
                    var running = WaitForProcessRunning("EpicGamesLauncher", 8000);
                    Globals.WriteToLog("[LAUNCHER] Executable launch requested. ProcessObject=" + (started != null) + "; RunningAfterWait=" + running);
                    return true;
                }
                Globals.WriteToLog("[LAUNCHER] Launcher executable not found. Starting by URI protocol.");
                var uriStarted = Process.Start(new ProcessStartInfo { FileName = "com.epicgames.launcher://", UseShellExecute = true });
                var uriRunning = WaitForProcessRunning("EpicGamesLauncher", 8000);
                Globals.WriteToLog("[LAUNCHER] URI launch requested. ProcessObject=" + (uriStarted != null) + "; RunningAfterWait=" + uriRunning);
                return true;
            }
            catch (Exception ex) { Globals.WriteToLog("[LAUNCHER] Failed to launch Epic Games Launcher", ex); return false; }
        }

        private static string ResolveEpicLauncherExecutable()
        {
            try
            {
                var installRoots = GetEpicInstallPaths();
                installRoots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Epic Games"));
                installRoots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic Games"));

                foreach (var root in installRoots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var candidates = new[]
                    {
                        Path.Combine(root, "Launcher", "Portal", "Binaries", "Win64", "EpicGamesLauncher.exe"),
                        Path.Combine(root, "Launcher", "Portal", "Binaries", "Win32", "EpicGamesLauncher.exe"),
                        Path.Combine(root, "EpicGamesLauncher.exe")
                    };
                    foreach (var candidate in candidates) if (File.Exists(candidate)) return candidate;
                }
            }
            catch { }
            return null;
        }

        public static async Task<bool> WaitForLaunchedAccountAsync(string expectedAccountId, Action<string> progress = null, int timeoutMs = 45000, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(expectedAccountId)) return false;

            var sw = Stopwatch.StartNew();
            DateTime? healthySince = null;
            var launchRequested = false;
            var lastStatus = "";
            var lastStatusLogAt = -10000L;

            Globals.WriteToLog("[VERIFY] Waiting for launched account. Expected=" + Globals.SafeLogId(expectedAccountId) + "; TimeoutMs=" + timeoutMs);

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var launcherRunning = Globals.IsProcessRunning("EpicGamesLauncher");
                if (!launcherRunning)
                {
                    if (!launchRequested)
                    {
                        progress?.Invoke(L.T("Launching Epic Games..."));
                        var launchOk = AutoLaunchEpicLauncher(false);
                        Globals.WriteToLog("[VERIFY] Launcher was not running. LaunchRequestAccepted=" + launchOk);
                        launchRequested = true;
                    }
                    healthySince = null;
                    var stoppedStatus = "LauncherRunning=False; Current=NULL; SessionValid=NULL";
                    if (!string.Equals(stoppedStatus, lastStatus, StringComparison.Ordinal) || sw.ElapsedMilliseconds - lastStatusLogAt >= 5000)
                    {
                        Globals.WriteToLog("[VERIFY] Poll " + stoppedStatus + "; ElapsedMs=" + sw.ElapsedMilliseconds);
                        lastStatus = stoppedStatus;
                        lastStatusLogAt = sw.ElapsedMilliseconds;
                    }
                    await Task.Delay(500, cancellationToken);
                    continue;
                }

                var currentId = GetCurrentAccountId();
                var session = ValidateSession(EpicSavedPath, expectedAccountId);
                var status = "LauncherRunning=True; Current=" + Globals.SafeLogId(currentId) + "; " + ValidationSummary(session);
                if (!string.Equals(status, lastStatus, StringComparison.Ordinal) || sw.ElapsedMilliseconds - lastStatusLogAt >= 5000)
                {
                    Globals.WriteToLog("[VERIFY] Poll " + status + "; ElapsedMs=" + sw.ElapsedMilliseconds);
                    lastStatus = status;
                    lastStatusLogAt = sw.ElapsedMilliseconds;
                }
                if (string.Equals(currentId, expectedAccountId, StringComparison.OrdinalIgnoreCase) && session.IsValid)
                {
                    if (!healthySince.HasValue)
                    {
                        progress?.Invoke(L.T("Verifying Epic sign-in..."));
                        healthySince = DateTime.UtcNow;
                    }

                    if (DateTime.UtcNow - healthySince.Value >= TimeSpan.FromSeconds(5))
                    {
                        Globals.WriteToLog("[VERIFY] Epic launcher is running with expected account: " + Globals.SafeLogId(expectedAccountId) + "; ElapsedMs=" + sw.ElapsedMilliseconds);
                        return true;
                    }
                }
                else
                {
                    healthySince = null;
                }

                await Task.Delay(1000, cancellationToken);
            }

            var finalSession = ValidateSession(EpicSavedPath, expectedAccountId);
            Globals.WriteToLog("[VERIFY] Timed out waiting for launched account. Expected=" + Globals.SafeLogId(expectedAccountId) + "; Current=" + Globals.SafeLogId(GetCurrentAccountId()) + "; LauncherRunning=" + Globals.IsProcessRunning("EpicGamesLauncher") + "; " + ValidationSummary(finalSession) + "; ElapsedMs=" + sw.ElapsedMilliseconds);
            return false;
        }

        public static async Task<bool> SwitchToAccount(string accId, Action<string> progress = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            var operationId = GetShortId();
            var sw = Stopwatch.StartNew();
            await SessionOperationLock.WaitAsync(cancellationToken);
            try
            {
                SetLastSwitchFailure("");
                cancellationToken.ThrowIfCancellationRequested();
                LogSwitch(operationId, "BEGIN Target=" + Globals.SafeLogId(accId));
                var accs = GetAccounts();
                LogSwitch(operationId, "Accounts loaded. Count=" + accs.Count);
                var t = accs.FirstOrDefault(a => string.Equals(a.Id, accId, StringComparison.OrdinalIgnoreCase));
                if (t == null)
                {
                    SetLastSwitchFailure(L.T("Target account is not saved."));
                    LogSwitch(operationId, "FAILED Target account not found.");
                    return false;
                }
                NormalizeAccount(t);
                var src = Path.Combine(BaseDataPath, t.Id);
                LogSwitch(operationId, "Target resolved. Target=" + Globals.SafeLogId(t.Id) + "; SourceState=" + PathStateSummary(src));
                if (!Directory.Exists(src))
                {
                    SetLastSwitchFailure(L.T("Saved account session folder is missing."));
                    LogSwitch(operationId, "FAILED Source folder missing. Source=" + src);
                    return false;
                }
                var sourceValidation = ValidateSession(src, t.Id);
                LogSwitch(operationId, "Source validation. " + ValidationSummary(sourceValidation));
                if (!sourceValidation.IsValid)
                {
                    SetLastSwitchFailure(L.T("The saved sign-in of this account is no longer valid: {0}", sourceValidation.Message));
                    LogSwitch(operationId, "FAILED Source validation rejected target session.");
                    return false;
                }
                var curB = GetCurrentAccountId();
                LogSwitch(operationId, "Current live account before close=" + Globals.SafeLogId(curB));
                // The launcher is force-stopped below and never saves its own window position, so read it from the live window now.
                var keepWindowPlacement = AppSettings.Instance.KeepLauncherWindowPlacement;
                var liveWindow = keepWindowPlacement ? LauncherWindowPlacement.Capture() : null;
                LogSwitch(operationId, "Launcher window before close: " + (liveWindow == null ? "none" : (liveWindow.Right - liveWindow.Left) + "x" + (liveWindow.Bottom - liveWindow.Top) + " at " + liveWindow.Left + "," + liveWindow.Top + (liveWindow.Maximized ? " (maximized)" : "")));
                progress?.Invoke(L.T("Closing Epic Games..."));
                if (!await EnsureEpicIsClosed(60000, cancellationToken))
                {
                    SetLastSwitchFailure(L.T("Epic Games Launcher or one of its helper processes could not be closed."));
                    LogSwitch(operationId, "FAILED Epic process cleanup timed out.");
                    return false;
                }
                LogSwitch(operationId, "Epic process cleanup completed. ElapsedMs=" + sw.ElapsedMilliseconds);
                // Placement to carry into the target account: the stored value of the live config (taken before it is swapped
                // out), corrected with the rectangle the launcher window really had a moment ago.
                var liveWindowPlacement = keepWindowPlacement ? GetLauncherWindowSettingsByPath() : null;
                if (liveWindowPlacement != null && liveWindow != null)
                {
                    foreach (var path in liveWindowPlacement.Keys.ToList())
                        liveWindowPlacement[path] = LauncherWindowPlacement.Apply(liveWindowPlacement[path], liveWindow);
                }
                await RefreshSnapshotOfCurrentSavedAccount(accs, t.Id, curB, progress, cancellationToken);
                LogSwitch(operationId, "Current account snapshot refresh completed.");
                var rbB = Path.Combine(BaseDataPath, "_switch_rb");
                var tmpB = Path.Combine(BaseDataPath, "_tmp_res" + GetShortId());
                var registryRollback = CaptureRegistryData();
                LogSwitch(operationId, "Registry rollback captured. Keys=" + (registryRollback == null ? 0 : registryRollback.Count));
                var credentialRollback = CredentialService.CaptureCredentials(curB);
                LogSwitch(operationId, "Credential rollback captured. Count=" + (credentialRollback == null ? 0 : credentialRollback.Count));
                try
                {
                    progress?.Invoke(L.T("Preparing session..."));
                    LogSwitch(operationId, "Preparing target session. RollbackState=" + PathStateSummary(rbB) + "; TempState=" + PathStateSummary(tmpB));
                    if (Directory.Exists(rbB)) await SafeDeleteDirectoryAsync(rbB, 10, cancellationToken);
                    Directory.CreateDirectory(rbB);
                    Directory.CreateDirectory(tmpB);
                    var tS = Path.Combine(tmpB, "Saved");
                    Directory.CreateDirectory(tS);
                    var copiedSessionItems = 0;
                    var missingSessionItems = 0;
                    foreach (var item in SessionItemsToCapture)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var s = Path.Combine(src, item);
                        var d = Path.Combine(tS, item);
                        if (Directory.Exists(s)) { await CopyDirectoryStrictAsync(s, d, cancellationToken); copiedSessionItems++; }
                        else if (File.Exists(s)) { await SafeReplaceFileAsync(s, d, cancellationToken); copiedSessionItems++; }
                        else missingSessionItems++;
                    }
                    var copiedAuthFiles = 0;
                    var missingAuthFiles = 0;
                    foreach (var f in AuthFiles.Concat(AuthBinaryFiles))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var sf = Path.Combine(src, f);
                        if (File.Exists(sf)) { await SafeReplaceFileAsync(sf, Path.Combine(tS, f), cancellationToken); copiedAuthFiles++; }
                        else missingAuthFiles++;
                    }
                    LogSwitch(operationId, "Prepared Saved payload. SessionItemsCopied=" + copiedSessionItems + "; SessionItemsMissing=" + missingSessionItems + "; AuthFilesCopied=" + copiedAuthFiles + "; AuthFilesMissing=" + missingAuthFiles + "; SavedState=" + PathStateSummary(tS));
                    var tE = Path.Combine(tmpB, "EOS_Data");
                    var sE = Path.Combine(src, "EOS_Data");
                    if (Directory.Exists(sE)) await CopyDirectoryStrictAsync(sE, tE, cancellationToken);
                    LogSwitch(operationId, "Prepared EOS payload. Source=" + PathStateSummary(sE) + "; Temp=" + PathStateSummary(tE));
                    var tR = Path.Combine(tmpB, "Roaming_Data");
                    var sR = Path.Combine(src, "Roaming_Data");
                    if (Directory.Exists(sR)) await CopyDirectoryStrictAsync(sR, tR, cancellationToken);
                    LogSwitch(operationId, "Prepared Roaming payload. Source=" + PathStateSummary(sR) + "; Temp=" + PathStateSummary(tR));
                    var tP = Path.Combine(tmpB, "ProgramData_Data", "Manifests");
                    var sP = Path.Combine(src, "ProgramData_Data", "Manifests");
                    if (Directory.Exists(sP)) await CopyDirectoryStrictAsync(sP, tP, cancellationToken);
                    LogSwitch(operationId, "Prepared ProgramData payload. Source=" + PathStateSummary(sP) + "; Temp=" + PathStateSummary(tP));
                    var preparedValidation = ValidateSession(tS, t.Id);
                    LogSwitch(operationId, "Prepared validation. " + ValidationSummary(preparedValidation));
                    if (!preparedValidation.IsValid) throw new InvalidDataException(preparedValidation.Message);
                    LogSwitch(operationId, "Moving live state to rollback.");
                    await MovePathToRollback(EpicSavedPath, rbB, "Saved", cancellationToken);
                    foreach (var rel in EOSFoldersToCapture)
                    {
                        await MovePathToRollback(Path.Combine(LocalAppData, "Epic Games", rel), rbB, "EOS_" + rel.Replace("\\", "_"), cancellationToken);
                        await MovePathToRollback(Path.Combine(LocalAppData, rel), rbB, "EOS_R_" + rel.Replace("\\", "_"), cancellationToken);
                    }
                    await MovePathToRollback(Path.Combine(RoamingAppData, "Epic Games", "EpicGamesLauncher"), rbB, "Roaming", cancellationToken);
                    if (Directory.Exists(tP)) await MovePathToRollback(ProgramDataManifestsPath, rbB, "PD_Manifests", cancellationToken);
                    LogSwitch(operationId, "Rollback prepared. RollbackState=" + PathStateSummary(rbB));
                    Directory.CreateDirectory(Path.GetDirectoryName(EpicSavedPath));
                    await SafeMovePathAsync(tS, EpicSavedPath, cancellationToken);
                    LogSwitch(operationId, "Restored Saved state. LiveSaved=" + PathStateSummary(EpicSavedPath));
                    if (Directory.Exists(tE)) await RestoreEOSData(tE, cancellationToken);
                    LogSwitch(operationId, "Restored EOS state. TempRemaining=" + PathStateSummary(tE));
                    if (Directory.Exists(tR)) await RestoreRoamingData(tR, cancellationToken);
                    LogSwitch(operationId, "Restored Roaming state. TempRemaining=" + PathStateSummary(tR));
                    if (Directory.Exists(tP)) { Directory.CreateDirectory(Path.GetDirectoryName(ProgramDataManifestsPath)); await SafeMovePathAsync(tP, ProgramDataManifestsPath, cancellationToken); }
                    LogSwitch(operationId, "Restored ProgramData manifests. LiveProgramData=" + PathStateSummary(ProgramDataManifestsPath));
                    RestoreRegistryIdentity(t);
                    LogSwitch(operationId, "Registry identity restored. TargetRegistryKeys=" + (t.RegistryData == null ? 0 : t.RegistryData.Count));
                    var deletedCurrentCredentials = CredentialService.DeleteCredentials(credentialRollback);
                    var restoredTargetCredentials = true;
                    if (t.Credentials != null && t.Credentials.Count > 0) restoredTargetCredentials = CredentialService.RestoreCredentials(t.Credentials);
                    LogSwitch(operationId, "Credentials swapped. DeletedCurrent=" + deletedCurrentCredentials + "; TargetCredentialCount=" + (t.Credentials == null ? 0 : t.Credentials.Count) + "; RestoredTarget=" + restoredTargetCredentials);
                    SetEmailSurgically(t.Email);
                    SetRememberMeDataSurgically(t);
                    var keptWindowPlacement = ApplyKeptLauncherWindowSettings(liveWindowPlacement);
                    if (!keptWindowPlacement) SetLauncherWindowSettingsSurgically(t);
                    LogSwitch(operationId, "Launcher config patched. KeptWindowPlacement=" + keptWindowPlacement + "; RememberMePaths=" + (t.RememberMeDataByPath == null ? 0 : t.RememberMeDataByPath.Count) + "; WindowSettingPaths=" + (t.LauncherWindowSettingsByPath == null ? 0 : t.LauncherWindowSettingsByPath.Count));
                    var liveValidation = ValidateRestoredState(t);
                    LogSwitch(operationId, "Live validation. " + ValidationSummary(liveValidation));
                    if (!liveValidation.IsValid) throw new InvalidDataException(liveValidation.Message);
                    StampAccountUsage(t, true);
                    MarkActiveAccount(accs, t.Id);
                    SaveAccounts(accs);
                    LogSwitch(operationId, "Account metadata saved. UsageCount=" + t.UsageCount + "; LastPlayed=" + (t.HasUsageHistory ? t.LastPlayed.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "NULL"));
                    await SafeDeleteDirectoryAsync(rbB, 10, cancellationToken);
                    await SafeDeleteDirectoryAsync(tmpB, 10, cancellationToken);
                    LogSwitch(operationId, "Temporary folders cleaned. RollbackExists=" + Directory.Exists(rbB) + "; TempExists=" + Directory.Exists(tmpB));
                    progress?.Invoke(L.T("Launching Epic Games..."));
                    var launchAccepted = AutoLaunchEpicLauncher(false);
                    LogSwitch(operationId, "END SUCCESS. LaunchRequestAccepted=" + launchAccepted + "; ElapsedMs=" + sw.ElapsedMilliseconds);
                    return true;
                }
                catch (OperationCanceledException)
                {
                    SetLastSwitchFailure(L.T("Switch was cancelled."));
                    LogSwitch(operationId, "CANCELLED. Starting rollback. ElapsedMs=" + sw.ElapsedMilliseconds);
                    await RollbackSwitchAsync(rbB);
                    RestoreRegistrySnapshot(registryRollback);
                    if (CredentialService.DeleteCredentials(t.Credentials ?? new List<CredentialService.SavedCredential>()) && credentialRollback != null) CredentialService.RestoreCredentials(credentialRollback);
                    if (Directory.Exists(tmpB)) await SafeDeleteDirectoryAsync(tmpB);
                    LogSwitch(operationId, "Rollback after cancellation completed.");
                    throw;
                }
                catch (Exception ex)
                {
                    SetLastSwitchFailure(L.T("The switch failed while restoring the account, and the previous sign-in was put back: {0}", ex.Message));
                    LogSwitch(operationId, "FAILED during restore. Starting rollback. RollbackState=" + PathStateSummary(rbB) + "; TempState=" + PathStateSummary(tmpB), ex);
                    await RollbackSwitchAsync(rbB);
                    RestoreRegistrySnapshot(registryRollback);
                    if (CredentialService.DeleteCredentials(t.Credentials ?? new List<CredentialService.SavedCredential>()) && credentialRollback != null) CredentialService.RestoreCredentials(credentialRollback);
                    if (Directory.Exists(tmpB)) await SafeDeleteDirectoryAsync(tmpB);
                    LogSwitch(operationId, "Rollback after restore failure completed. LiveSaved=" + PathStateSummary(EpicSavedPath));
                    return false;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                SetLastSwitchFailure(L.T("The switch could not start: {0}", ex.Message));
                LogSwitch(operationId, "FAILED before restore phase. ElapsedMs=" + sw.ElapsedMilliseconds, ex);
                return false;
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(LastSwitchFailureMessage))
                    LogSwitch(operationId, "END FAILED. Reason=" + LastSwitchFailureMessage + "; ElapsedMs=" + sw.ElapsedMilliseconds);
                SessionOperationLock.Release();
            }
        }

        private static async Task MovePathToRollback(string source, string rollbackRoot, string rollbackName, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!Directory.Exists(source) && !File.Exists(source)) return;
            var rb = Path.Combine(rollbackRoot, rollbackName);
            Directory.CreateDirectory(Path.GetDirectoryName(rb));
            if (Directory.Exists(rb)) await SafeDeleteDirectoryAsync(rb, 10, cancellationToken);
            if (File.Exists(rb)) File.Delete(rb);
            await SafeMovePathAsync(source, rb, cancellationToken);
        }

        private static async Task RestoreEOSData(string sourceRoot, CancellationToken cancellationToken = default(CancellationToken))
        {
            foreach (var rel in EOSFoldersToCapture)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var s = Path.Combine(sourceRoot, rel);
                if (!Directory.Exists(s)) continue;
                var dest = File.Exists(s + ".is_root") ? Path.Combine(LocalAppData, rel) : Path.Combine(LocalAppData, "Epic Games", rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                await SafeMovePathAsync(s, dest, cancellationToken);
            }
        }

        private static async Task RestoreRoamingData(string sourceRoot, CancellationToken cancellationToken = default(CancellationToken))
        {
            foreach (var folder in Directory.GetDirectories(sourceRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dest = Path.Combine(RoamingAppData, "Epic Games", Path.GetFileName(folder));
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                await SafeMovePathAsync(folder, dest, cancellationToken);
            }
        }

        private static async Task RollbackSwitchAsync(string rollbackRoot)
        {
            try
            {
                await RestoreRollbackPath(rollbackRoot, "Saved", EpicSavedPath);
                foreach (var rel in EOSFoldersToCapture)
                {
                    await RestoreRollbackPath(rollbackRoot, "EOS_" + rel.Replace("\\", "_"), Path.Combine(LocalAppData, "Epic Games", rel));
                    await RestoreRollbackPath(rollbackRoot, "EOS_R_" + rel.Replace("\\", "_"), Path.Combine(LocalAppData, rel));
                }
                await RestoreRollbackPath(rollbackRoot, "Roaming", Path.Combine(RoamingAppData, "Epic Games", "EpicGamesLauncher"));
                await RestoreRollbackPath(rollbackRoot, "PD_Manifests", ProgramDataManifestsPath);
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("[ROLLBACK] Switch rollback failed.", ex);
            }
        }

        private static async Task RestoreRollbackPath(string rollbackRoot, string name, string target)
        {
            var s = Path.Combine(rollbackRoot, name);
            if (!Directory.Exists(s) && !File.Exists(s)) return;
            if (Directory.Exists(target)) await SafeDeleteDirectoryAsync(target);
            else if (File.Exists(target)) File.Delete(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            await SafeMovePathAsync(s, target);
        }

        private static SessionValidationResult ValidateRestoredState(Account target)
        {
            var live = ValidateSession(EpicSavedPath, target.Id);
            if (!live.IsValid) return live;
            if (!VerifyRegistryIdentity(target.Id)) return new SessionValidationResult { IsValid = false, Message = "Restored registry identity does not match target account." };
            if (!VerifyRememberMeApplied(target)) return new SessionValidationResult { IsValid = false, Message = "Restored RememberMe state does not match target account." };
            var restoredAuthFiles = AuthFiles.Select(f => Path.Combine(EpicSavedPath, f)).Where(File.Exists).ToList();
            var hasModernBinary = File.Exists(Path.Combine(EpicSavedPath, "Data", "OC_" + target.Id + ".dat"));
            if (restoredAuthFiles.Count == 0 && !hasModernBinary) return new SessionValidationResult { IsValid = false, Message = "No restored auth file exists." };
            live.IsValid = true;
            return live;
        }

        private static readonly object _metadataLock = new object();
        private static Tuple<string, string, string> _lastDetected;

        public static async Task<Tuple<string, string, string>> GetDetectedMetadataAsync()
        {
            return await Task.Run(() => GetDetectedMetadata());
        }

        public static Tuple<string, string, string> GetDetectedMetadata()
        {
                var id = GetCurrentAccountId();
                lock (_metadataLock)
                {
                    if (!string.IsNullOrEmpty(id) && _lastDetected != null && _lastDetected.Item1 == id && _lastDetected.Item2 != "N/A") return _lastDetected;
                }
                var name = "N/A";
                var email = "N/A";
                try
                {
                    if (Directory.Exists(EpicConfigPath))
                    {
                        var files = Directory.GetFiles(EpicConfigPath, "GameUserSettings.ini", SearchOption.AllDirectories);
                        var ini = files.FirstOrDefault(f => f.IndexOf("WindowsEditor", StringComparison.OrdinalIgnoreCase) >= 0) ?? files.FirstOrDefault();
                        if (ini != null)
                        {
                            var lines = ReadAllLinesSafe(ini);
                            foreach (var raw in lines)
                            {
                                var line = raw.Trim();
                                if (line.StartsWith("LastCheckedEmail=", StringComparison.OrdinalIgnoreCase)) { var v = line.Substring("LastCheckedEmail=".Length).Trim(); if (!string.IsNullOrWhiteSpace(v)) email = v; }
                                else if (line.StartsWith("DisplayName=", StringComparison.OrdinalIgnoreCase)) { var v = line.Substring("DisplayName=".Length).Trim(); if (!string.IsNullOrWhiteSpace(v)) name = v; }
                            }
                        }
                    }
                    if (name == "N/A" || email == "N/A")
                    {
                        var logsPath = Path.Combine(EpicSavedPath, "Logs");
                        if (Directory.Exists(logsPath))
                        {
                            var logs = Directory.GetFiles(logsPath, "EpicGamesLauncher*.log").OrderByDescending(File.GetLastWriteTimeUtc).Take(2).ToList();
                            foreach (var f in logs)
                            {
                                try
                                {
                                    var lines = ReadAllLinesSafe(f);
                                    var q = new Queue<string>(1000);
                                    foreach (var l in lines) { if (q.Count >= 1000) q.Dequeue(); q.Enqueue(l ?? ""); }
                                    foreach (var line in q.Reverse())
                                    {
                                        if (name == "N/A" && line.Contains("-epicusername=\""))
                                        {
                                            var k = "-epicusername=\"";
                                            var s = line.IndexOf(k, StringComparison.Ordinal);
                                            if (s >= 0) { s += k.Length; var e = line.IndexOf('"', s); if (e > s) { var v = line.Substring(s, e - s).Trim(); if (!string.IsNullOrWhiteSpace(v)) name = v; } }
                                        }
                                        if (name == "N/A" && line.Contains("DisplayName: "))
                                        {
                                            var idx = line.IndexOf("DisplayName: ", StringComparison.Ordinal);
                                            if (idx >= 0) { var v = line.Substring(idx + "DisplayName: ".Length).Trim(); if (!string.IsNullOrWhiteSpace(v)) name = v; }
                                        }
                                        if (email == "N/A" && line.Contains("@")) { var m = Regex.Match(line, @"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}"); if (m.Success) email = m.Value; }
                                        if (name != "N/A" && email != "N/A") break;
                                    }
                                }
                                catch { }
                                if (name != "N/A" && email != "N/A") break;
                            }
                        }
                    }
                }
                catch { }
                var res = new Tuple<string, string, string>(id, name, email);
                lock (_metadataLock) { _lastDetected = res; }
                return res;
        }

        public static async Task<Tuple<string, string, string>> WaitForDetectedMetadata(int sec = 120, int interval = 1500)
        {
            var end = DateTime.UtcNow.AddSeconds(sec);
            while (DateTime.UtcNow < end) { var det = GetDetectedMetadata(); if (!string.IsNullOrWhiteSpace(det.Item1)) return det; await Task.Delay(interval); }
            return new Tuple<string, string, string>(null, "N/A", "N/A");
        }
    }
}
