using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Compression;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Epic_Games_Account_Switcher_Globals
{
    public class AccountService
    {
        private static readonly object AccountsFileLock = new();
        private static readonly SemaphoreSlim SessionOperationLock = new(1, 1);

        private static string BaseDataPath => Path.Combine(Globals.UserDataFolder, "LoginCache", "Epic_Games");
        private static string AccountsJsonPath => Path.Combine(BaseDataPath, "accounts.json");
        private static List<string> RegistryKeysToSwap => AppSettings.Instance.RegistryKeysToSwap;

        private const string LegacyDefaultProfileImagePackUri = "pack://application:,,,/Skins_Icons/Default_Skin_Guy.jpg";
        private const string DefaultProfileImageFileName = "Default_Skin_Guy.jpg";
        private static string _cachedDefaultProfileImagePath;

        public static string GetDefaultProfileImagePath()
        {
            if (!string.IsNullOrWhiteSpace(_cachedDefaultProfileImagePath))
            {
                if (_cachedDefaultProfileImagePath.StartsWith("pack://", StringComparison.OrdinalIgnoreCase) || File.Exists(_cachedDefaultProfileImagePath))
                    return _cachedDefaultProfileImagePath;
            }

            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Skins_Icons", DefaultProfileImageFileName),
                Path.Combine(Path.GetFullPath("Payload\\Skins_Icons"), DefaultProfileImageFileName),
                Path.Combine(Path.GetFullPath("Skin_Icons"), DefaultProfileImageFileName)
            };

            foreach (var candidate in candidates)
            {
                try
                {
                    if (File.Exists(candidate))
                    {
                        _cachedDefaultProfileImagePath = candidate;
                        return _cachedDefaultProfileImagePath;
                    }
                }
                catch
                {
                }
            }

            _cachedDefaultProfileImagePath = LegacyDefaultProfileImagePackUri;
            return _cachedDefaultProfileImagePath;
        }

        private static string NormalizeProfileImagePath(string profileImagePath)
        {
            var defaultPath = GetDefaultProfileImagePath();

            if (string.IsNullOrWhiteSpace(profileImagePath))
                return defaultPath;

            if (string.Equals(profileImagePath, LegacyDefaultProfileImagePackUri, StringComparison.OrdinalIgnoreCase))
                return defaultPath;

            try
            {
                if (File.Exists(profileImagePath))
                    return profileImagePath;

                if (!Path.IsPathRooted(profileImagePath))
                {
                    var baseCandidate = Path.Combine(AppContext.BaseDirectory, profileImagePath);
                    if (File.Exists(baseCandidate))
                        return baseCandidate;

                    var cwdCandidate = Path.GetFullPath(profileImagePath);
                    if (File.Exists(cwdCandidate))
                        return cwdCandidate;
                }
            }
            catch
            {
            }

            return defaultPath;
        }

        private static readonly string[] SessionFoldersToCapture =
        {
            "Common",
            "SocialCache",
            "Manifests",
            "Config",
            "Data",
            "UserVaultSettings"
        };

        public static List<Account> GetAccounts()
        {
            lock (AccountsFileLock)
            {
                if (!File.Exists(AccountsJsonPath)) return new List<Account>();

                try
                {
                    var accounts = JsonConvert.DeserializeObject<List<Account>>(File.ReadAllText(AccountsJsonPath)) ?? new List<Account>();
                    foreach (var acc in accounts) NormalizeAccount(acc);
                    return accounts;
                }
                catch (Exception ex)
                {
                    Globals.WriteToLog("Failed to load accounts", ex);
                    return new List<Account>();
                }
            }
        }

        public static void SaveAccounts(List<Account> accounts)
        {
            lock (AccountsFileLock)
            {
                Directory.CreateDirectory(BaseDataPath);
                foreach (var acc in accounts) NormalizeAccount(acc);

                var tmpPath = AccountsJsonPath + ".tmp";
                File.WriteAllText(tmpPath, JsonConvert.SerializeObject(accounts, Formatting.Indented));

                if (File.Exists(AccountsJsonPath)) File.Delete(AccountsJsonPath);
                File.Move(tmpPath, AccountsJsonPath);
            }
        }

        private static void NormalizeAccount(Account acc)
        {
            if (acc == null) return;

            acc.Name ??= "Unknown Account";
            acc.Email ??= "Unknown Email";
            acc.Description ??= string.Empty;
            acc.AccentColor = string.IsNullOrWhiteSpace(acc.AccentColor) ? "#7AA2F7" : acc.AccentColor;
            acc.ProfileImagePath = NormalizeProfileImagePath(acc.ProfileImagePath);
            acc.SourceWebCacheName = string.IsNullOrWhiteSpace(acc.SourceWebCacheName) ? "webcache" : acc.SourceWebCacheName;
            acc.RegistryData ??= new Dictionary<string, string>();
        }

        private static string GetEpicSavedPath()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "EpicGamesLauncher", "Saved");
        }

        private static (string Path, string Name) GetEpicWebCacheInfo(string epicSavedPath)
        {
            if (!Directory.Exists(epicSavedPath)) return (null, "webcache");

            var preferred = Path.Combine(epicSavedPath, "webcache");
            if (Directory.Exists(preferred)) return (preferred, "webcache");

            var webCacheVariants = Directory.GetDirectories(epicSavedPath, "webcache*");
            var best = webCacheVariants
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .FirstOrDefault();

            return best == null ? (null, "webcache") : (best, Path.GetFileName(best));
        }

        public static string GetCurrentAccountId()
        {
            try
            {
                if (Globals.TryReadRegistryKey(@"HKCU\Software\Epic Games\Unreal Engine\Identifiers:AccountId", out var accountId))
                    return accountId?.ToString();
            }
            catch { }

            return null;
        }

        private static async Task<bool> EnsureEpicIsClosed(int timeoutMs = 45000)
        {
            var sw = Stopwatch.StartNew();
            var processesToKill = new[] { "EpicGamesLauncher", "EpicWebHelper", "EpicGamesLauncherService" };

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                var anyRunning = false;

                foreach (var procName in processesToKill)
                {
                    if (!Globals.IsProcessRunning(procName)) continue;
                    anyRunning = true;
                    Globals.KillProcess(procName, true);
                }

                if (!anyRunning)
                {
                    var loginCache = Path.Combine(GetEpicSavedPath(), "LoginCache.json");
                    if (!File.Exists(loginCache)) return true;

                    try
                    {
                        using var _ = new FileStream(loginCache, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                        return true;
                    }
                    catch (IOException)
                    {
                        
                    }
                }

                await Task.Delay(1200);
            }

            return false;
        }

        private static async Task<bool> SafeDeleteDirectoryAsync(string path, int retries = 10)
        {
            if (!Directory.Exists(path)) return true;

            for (var i = 0; i < retries; i++)
            {
                try
                {
                    Globals.ForceDeleteDirectory(path);
                    return true;
                }
                catch
                {
                    await Task.Delay(500);
                }
            }

            return false;
        }

        private static async Task<bool> SafeReplaceDirectoryAsync(string sourceDir, string targetDir)
        {
            if (!Directory.Exists(sourceDir)) return true;

            var tempTarget = targetDir + "_swap_tmp";
            await SafeDeleteDirectoryAsync(tempTarget);

            Directory.CreateDirectory(tempTarget);
            await Globals.CopyFilesRecursiveAsync(sourceDir, tempTarget, true);

            await SafeDeleteDirectoryAsync(targetDir);
            Directory.CreateDirectory(Path.GetDirectoryName(targetDir) ?? string.Empty);
            Directory.Move(tempTarget, targetDir);
            return true;
        }

        private static void SafeReplaceFile(string source, string target)
        {
            if (!File.Exists(source)) return;

            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? string.Empty);

            var tempTarget = target + ".swap_tmp";
            if (File.Exists(tempTarget)) File.Delete(tempTarget);

            File.Copy(source, tempTarget, true);
            if (File.Exists(target)) File.Delete(target);
            File.Move(tempTarget, target);
        }

        private static async Task CaptureSessionToFolder(string epicSavedPath, string sourceWebCachePath, string destinationFolder)
        {
            await SafeDeleteDirectoryAsync(destinationFolder);
            Directory.CreateDirectory(destinationFolder);

            var sourceLoginCache = Path.Combine(epicSavedPath, "LoginCache.json");
            if (File.Exists(sourceLoginCache))
            {
                SafeReplaceFile(sourceLoginCache, Path.Combine(destinationFolder, "LoginCache.json"));
            }

            if (!string.IsNullOrWhiteSpace(sourceWebCachePath) && Directory.Exists(sourceWebCachePath))
            {
                await SafeReplaceDirectoryAsync(sourceWebCachePath, Path.Combine(destinationFolder, "webcache"));
            }

            foreach (var folder in SessionFoldersToCapture)
            {
                var sourcePath = Path.Combine(epicSavedPath, folder);
                var destPath = Path.Combine(destinationFolder, folder);
                if (Directory.Exists(sourcePath))
                {
                    await SafeReplaceDirectoryAsync(sourcePath, destPath);
                }
            }
        }

        private static Dictionary<string, string> CaptureRegistryData()
        {
            var data = new Dictionary<string, string>();
            foreach (var key in RegistryKeysToSwap)
            {
                try
                {
                    if (Globals.TryReadRegistryKey(key, out var value) && value != null)
                        data[key] = value.ToString();
                }
                catch { }
            }
            return data;
        }

        private static string GetDeterministicColor(string input)
        {
            var colors = new[] { "#7AA2F7", "#9ECE6A", "#BB9AF7", "#E0AF68", "#F7768E", "#7DCFFF", "#2AC3DE", "#B4F9F8" };
            var hash = 0;
            foreach (var ch in input ?? string.Empty) hash = (hash << 5) - hash + ch;
            return colors[Math.Abs(hash) % colors.Length];
        }

        public static async Task<bool> CaptureCurrentAccount(
            string nameOverride = null,
            string emailOverride = null,
            string imagePathOverride = null,
            string descriptionOverride = null,
            Action<string> progress = null,
            Func<string, Task<bool>> confirmOverwrite = null)
        {
            await SessionOperationLock.WaitAsync();
            try
            {
                var detected = GetDetectedMetadata();
                if (string.IsNullOrWhiteSpace(detected.Id))
                {
                    Globals.WriteToLog("[ERROR] Capture aborted: could not detect AccountId.");
                    return false;
                }

                progress?.Invoke("Closing Epic Games Launcher...");
                if (!await EnsureEpicIsClosed())
                {
                    Globals.WriteToLog("[ERROR] Capture aborted: failed to close Epic launcher in time.");
                    return false;
                }

                var accounts = GetAccounts();
                var existing = accounts.FirstOrDefault(a => a.Id == detected.Id);
                if (existing != null && nameOverride == null && confirmOverwrite != null)
                {
                    if (!await confirmOverwrite(existing.Name))
                        return false;
                }

                var epicSavedPath = GetEpicSavedPath();
                if (!Directory.Exists(epicSavedPath))
                {
                    Globals.WriteToLog("[ERROR] Capture aborted: Epic saved path is missing.");
                    return false;
                }

                var webCacheInfo = GetEpicWebCacheInfo(epicSavedPath);
                var account = existing ?? new Account(detected.Id, nameOverride ?? (detected.Name == "N/A" ? "Epic Account" : detected.Name));

                if (!string.IsNullOrWhiteSpace(nameOverride)) account.Name = nameOverride.Trim();
                else if (!string.IsNullOrWhiteSpace(detected.Name) && detected.Name != "N/A") account.Name = detected.Name;

                if (emailOverride != null) account.Email = emailOverride.Trim();
                else if (!string.IsNullOrWhiteSpace(detected.Email) && detected.Email != "N/A") account.Email = detected.Email;

                if (descriptionOverride != null) account.Description = descriptionOverride;
                if (!string.IsNullOrWhiteSpace(imagePathOverride)) account.ProfileImagePath = imagePathOverride;

                account.ProfileImagePath = NormalizeProfileImagePath(account.ProfileImagePath);
                account.AccentColor = GetDeterministicColor(account.Name ?? account.Id);
                account.SourceWebCacheName = string.IsNullOrWhiteSpace(webCacheInfo.Name) ? "webcache" : webCacheInfo.Name;

                progress?.Invoke("Capturing account session...");
                var targetFolder = Path.Combine(BaseDataPath, account.Id);
                var tempFolder = targetFolder + "_new_tmp";
                var backupFolder = targetFolder + "_old_bak";

                await CaptureSessionToFolder(epicSavedPath, webCacheInfo.Path, tempFolder);
                account.RegistryData = CaptureRegistryData();

                if (Directory.Exists(backupFolder)) await SafeDeleteDirectoryAsync(backupFolder);
                if (Directory.Exists(targetFolder)) Directory.Move(targetFolder, backupFolder);

                try
                {
                    Directory.Move(tempFolder, targetFolder);
                    if (Directory.Exists(backupFolder)) await SafeDeleteDirectoryAsync(backupFolder);
                }
                catch
                {
                    if (Directory.Exists(targetFolder)) await SafeDeleteDirectoryAsync(targetFolder);
                    if (Directory.Exists(backupFolder)) Directory.Move(backupFolder, targetFolder);
                    throw;
                }

                if (existing == null) accounts.Add(account);
                SaveAccounts(accounts);

                return true;
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("[FATAL] Capture failed.", ex);
                return false;
            }
            finally
            {
                SessionOperationLock.Release();
            }
        }

        private static async Task RefreshSnapshotOfCurrentSavedAccount(List<Account> accounts, string switchingToAccountId, string epicSavedPath, Action<string> progress)
        {
            var currentId = GetCurrentAccountId();
            if (string.IsNullOrWhiteSpace(currentId) || currentId == switchingToAccountId) return;

            var currentAccount = accounts.FirstOrDefault(a => a.Id == currentId);
            if (currentAccount == null) return;

            progress?.Invoke("Saving current account session...");

            var webCacheInfo = GetEpicWebCacheInfo(epicSavedPath);
            currentAccount.SourceWebCacheName = string.IsNullOrWhiteSpace(webCacheInfo.Name) ? "webcache" : webCacheInfo.Name;
            currentAccount.RegistryData = CaptureRegistryData();

            var targetFolder = Path.Combine(BaseDataPath, currentAccount.Id);
            var tempFolder = targetFolder + "_refresh_tmp";
            var backupFolder = targetFolder + "_refresh_bak";

            await CaptureSessionToFolder(epicSavedPath, webCacheInfo.Path, tempFolder);

            if (Directory.Exists(backupFolder)) await SafeDeleteDirectoryAsync(backupFolder);
            if (Directory.Exists(targetFolder)) Directory.Move(targetFolder, backupFolder);

            try
            {
                Directory.Move(tempFolder, targetFolder);
                if (Directory.Exists(backupFolder)) await SafeDeleteDirectoryAsync(backupFolder);
            }
            catch
            {
                if (Directory.Exists(targetFolder)) await SafeDeleteDirectoryAsync(targetFolder);
                if (Directory.Exists(backupFolder)) Directory.Move(backupFolder, targetFolder);
                throw;
            }

            SaveAccounts(accounts);
        }

        private static void MarkActiveAccount(List<Account> accounts, string activeId)
        {
            foreach (var acc in accounts)
            {
                acc.IsActive = string.Equals(acc.Id, activeId, StringComparison.OrdinalIgnoreCase);
            }
        }

        public static async Task<bool> SwitchToAccount(string accId, Action<string> progress = null)
        {
            await SessionOperationLock.WaitAsync();
            try
            {
                var accounts = GetAccounts();
                var target = accounts.FirstOrDefault(a => a.Id == accId);
                if (target == null)
                {
                    Globals.WriteToLog($"[ERROR] Switch aborted: account ID {accId} not found.");
                    return false;
                }

                progress?.Invoke("Closing Epic Games Launcher...");
                if (!await EnsureEpicIsClosed())
                {
                    Globals.WriteToLog("[ERROR] Switch aborted: failed to close Epic launcher in time.");
                    return false;
                }

                var epicSavedPath = GetEpicSavedPath();
                Directory.CreateDirectory(epicSavedPath);

                
                await RefreshSnapshotOfCurrentSavedAccount(accounts, target.Id, epicSavedPath, progress);

                var sourceFolder = Path.Combine(BaseDataPath, target.Id);
                if (!Directory.Exists(sourceFolder))
                {
                    Globals.WriteToLog($"[ERROR] Switch aborted: account data folder does not exist ({sourceFolder}).");
                    return false;
                }

                
                var safetyBackup = Path.Combine(BaseDataPath, "_last_session_bak");
                await SafeDeleteDirectoryAsync(safetyBackup);
                Directory.CreateDirectory(safetyBackup);
                await Globals.CopyFilesRecursiveAsync(epicSavedPath, safetyBackup, true);

                try
                {
                    progress?.Invoke("Restoring login session...");
                    SafeReplaceFile(Path.Combine(sourceFolder, "LoginCache.json"), Path.Combine(epicSavedPath, "LoginCache.json"));

                    var savedWebCache = Path.Combine(sourceFolder, "webcache");
                    if (Directory.Exists(savedWebCache))
                    {
                        var existingTargets = Directory.GetDirectories(epicSavedPath, "webcache*").ToList();
                        var preferredName = string.IsNullOrWhiteSpace(target.SourceWebCacheName) ? "webcache" : target.SourceWebCacheName;
                        var preferredPath = Path.Combine(epicSavedPath, preferredName);

                        if (!existingTargets.Any()) existingTargets.Add(preferredPath);
                        if (!existingTargets.Contains(preferredPath, StringComparer.OrdinalIgnoreCase)) existingTargets.Add(preferredPath);

                        foreach (var targetWebCache in existingTargets.Distinct(StringComparer.OrdinalIgnoreCase))
                        {
                            await SafeReplaceDirectoryAsync(savedWebCache, targetWebCache);
                        }
                    }

                    foreach (var folder in SessionFoldersToCapture)
                    {
                        var savedFolder = Path.Combine(sourceFolder, folder);
                        var targetFolder = Path.Combine(epicSavedPath, folder);
                        if (Directory.Exists(savedFolder))
                        {
                            await SafeReplaceDirectoryAsync(savedFolder, targetFolder);
                        }
                    }

                    progress?.Invoke("Applying account identity...");
                    if (target.RegistryData != null)
                    {
                        foreach (var kv in target.RegistryData)
                        {
                            Globals.SetRegistryKey(kv.Key, kv.Value);
                        }
                    }

                    MarkActiveAccount(accounts, target.Id);
                    SaveAccounts(accounts);

                    progress?.Invoke("Launching Epic Games...");
                    Process.Start(new ProcessStartInfo { FileName = "com.epicgames.launcher://", UseShellExecute = true });
                    return true;
                }
                catch (Exception ex)
                {
                    Globals.WriteToLog("[CRITICAL] Switch failed. Attempting rollback...", ex);
                    try
                    {
                        await SafeDeleteDirectoryAsync(epicSavedPath);
                        Directory.CreateDirectory(epicSavedPath);
                        await Globals.CopyFilesRecursiveAsync(safetyBackup, epicSavedPath, true);
                        Globals.WriteToLog("[SAFETY] Rollback completed.");
                    }
                    catch (Exception rollbackEx)
                    {
                        Globals.WriteToLog("[FATAL] Rollback failed.", rollbackEx);
                    }

                    return false;
                }
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("[FATAL] Unexpected switch exception.", ex);
                return false;
            }
            finally
            {
                SessionOperationLock.Release();
            }
        }

        public static (string Id, string Name, string Email) GetDetectedMetadata()
        {
            var detectedId = GetCurrentAccountId();
            var detectedName = "N/A";
            var detectedEmail = "N/A";

            var epicSavedPath = GetEpicSavedPath();
            var epicConfigPath = Path.Combine(epicSavedPath, "Config");

            try
            {
                if (Directory.Exists(epicConfigPath))
                {
                    var iniFiles = Directory.GetFiles(epicConfigPath, "GameUserSettings.ini", SearchOption.AllDirectories);
                    foreach (var ini in iniFiles)
                    {
                        foreach (var raw in File.ReadAllLines(ini))
                        {
                            var line = raw.Trim();

                            if (line.StartsWith("LastCheckedEmail=", StringComparison.OrdinalIgnoreCase))
                            {
                                var value = line.Substring("LastCheckedEmail=".Length).Trim();
                                if (!string.IsNullOrWhiteSpace(value)) detectedEmail = value;
                            }
                            else if (line.StartsWith("DisplayName=", StringComparison.OrdinalIgnoreCase))
                            {
                                var value = line.Substring("DisplayName=".Length).Trim();
                                if (!string.IsNullOrWhiteSpace(value)) detectedName = value;
                            }
                        }

                        if (detectedName != "N/A" && detectedEmail != "N/A") break;
                    }
                }

                if (detectedName == "N/A" || detectedEmail == "N/A")
                {
                    var logsPath = Path.Combine(epicSavedPath, "Logs");
                    if (Directory.Exists(logsPath))
                    {
                        var logs = Directory.GetFiles(logsPath, "EpicGamesLauncher*.log")
                            .OrderByDescending(File.GetLastWriteTimeUtc)
                            .Take(3)
                            .ToList();

                        foreach (var logFile in logs)
                        {
                            try
                            {
                                using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                                using var reader = new StreamReader(fs);
                                while (!reader.EndOfStream)
                                {
                                    var line = reader.ReadLine() ?? string.Empty;

                                    if (detectedName == "N/A" && line.Contains("-epicusername=\"", StringComparison.Ordinal))
                                    {
                                        var key = "-epicusername=\"";
                                        var start = line.IndexOf(key, StringComparison.Ordinal);
                                        if (start >= 0)
                                        {
                                            start += key.Length;
                                            var end = line.IndexOf('"', start);
                                            if (end > start)
                                            {
                                                var val = line.Substring(start, end - start).Trim();
                                                if (!string.IsNullOrWhiteSpace(val)) detectedName = val;
                                            }
                                        }
                                    }

                                    if (detectedName == "N/A" && line.Contains("DisplayName: ", StringComparison.Ordinal))
                                    {
                                        var idx = line.IndexOf("DisplayName: ", StringComparison.Ordinal);
                                        if (idx >= 0)
                                        {
                                            var val = line[(idx + "DisplayName: ".Length)..].Trim();
                                            if (!string.IsNullOrWhiteSpace(val)) detectedName = val;
                                        }
                                    }

                                    if (detectedEmail == "N/A" && line.Contains('@') && (line.Contains("Email", StringComparison.OrdinalIgnoreCase) || line.Contains("User", StringComparison.OrdinalIgnoreCase)))
                                    {
                                        var match = Regex.Match(line, @"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}");
                                        if (match.Success) detectedEmail = match.Value;
                                    }

                                    if (detectedName != "N/A" && detectedEmail != "N/A") break;
                                }
                            }
                            catch { }

                            if (detectedName != "N/A" && detectedEmail != "N/A") break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("Error gathering account metadata", ex);
            }

            return (detectedId, detectedName, detectedEmail);
        }

        public static async Task<(string Id, string Name, string Email)> WaitForDetectedMetadata(int timeoutSeconds = 120, int pollIntervalMs = 1500)
        {
            var timeoutAt = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < timeoutAt)
            {
                var detected = GetDetectedMetadata();
                if (!string.IsNullOrWhiteSpace(detected.Id)) return detected;
                await Task.Delay(pollIntervalMs);
            }

            return (null, "N/A", "N/A");
        }

        public static void BackupAccounts(string zipPath)
        {
            Directory.CreateDirectory(BaseDataPath);
            var tempFolder = Path.Combine(Path.GetTempPath(), "epic_accounts_backup_" + Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(tempFolder);

                if (File.Exists(AccountsJsonPath))
                {
                    File.Copy(AccountsJsonPath, Path.Combine(tempFolder, "accounts.json"), true);
                }

                foreach (var account in GetAccounts())
                {
                    var accountFolder = Path.Combine(BaseDataPath, account.Id);
                    if (!Directory.Exists(accountFolder)) continue;

                    var targetFolder = Path.Combine(tempFolder, account.Id);
                    Directory.CreateDirectory(targetFolder);
                    Globals.CopyFilesRecursive(accountFolder, targetFolder, true);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(zipPath) ?? string.Empty);
                if (File.Exists(zipPath)) File.Delete(zipPath);
                ZipFile.CreateFromDirectory(tempFolder, zipPath, CompressionLevel.Optimal, false);
            }
            finally
            {
                if (Directory.Exists(tempFolder))
                {
                    try { Globals.ForceDeleteDirectory(tempFolder); } catch { }
                }
            }
        }

        public static void RestoreAccounts(string zipPath)
        {
            if (!File.Exists(zipPath)) throw new FileNotFoundException("Backup file not found", zipPath);

            Directory.CreateDirectory(BaseDataPath);
            var restoreTemp = Path.Combine(BaseDataPath, "_restore_tmp_" + Guid.NewGuid().ToString("N"));
            var backupFolder = BaseDataPath + "_restore_backup";

            try
            {
                Directory.CreateDirectory(restoreTemp);
                ZipFile.ExtractToDirectory(zipPath, restoreTemp, true);

                var restoredAccountsJson = Path.Combine(restoreTemp, "accounts.json");
                if (!File.Exists(restoredAccountsJson))
                    throw new InvalidDataException("Backup is missing accounts.json");

                var restoredAccounts = JsonConvert.DeserializeObject<List<Account>>(File.ReadAllText(restoredAccountsJson)) ?? new List<Account>();
                foreach (var acc in restoredAccounts) NormalizeAccount(acc);

                if (Directory.Exists(backupFolder)) Globals.ForceDeleteDirectory(backupFolder);
                if (Directory.Exists(BaseDataPath)) Directory.Move(BaseDataPath, backupFolder);

                Directory.CreateDirectory(BaseDataPath);
                Globals.CopyFilesRecursive(restoreTemp, BaseDataPath, true);

                SaveAccounts(restoredAccounts);

                if (Directory.Exists(backupFolder)) Globals.ForceDeleteDirectory(backupFolder);
            }
            catch
            {
                if (Directory.Exists(BaseDataPath))
                {
                    try { Globals.ForceDeleteDirectory(BaseDataPath); } catch { }
                }

                if (Directory.Exists(backupFolder))
                {
                    Directory.Move(backupFolder, BaseDataPath);
                }

                throw;
            }
            finally
            {
                if (Directory.Exists(restoreTemp))
                {
                    try { Globals.ForceDeleteDirectory(restoreTemp); } catch { }
                }
            }
        }

        public static void DeleteAccount(string accId)
        {
            var accounts = GetAccounts();
            var target = accounts.FirstOrDefault(a => a.Id == accId);
            if (target == null) return;

            accounts.Remove(target);
            SaveAccounts(accounts);

            var accountFolder = Path.Combine(BaseDataPath, target.Id);
            if (Directory.Exists(accountFolder))
            {
                try { Globals.ForceDeleteDirectory(accountFolder); } catch { }
            }
        }
    }
}