using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.Win32;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;

namespace Epic_Games_Account_Switcher_Globals
{
    public class AccountService
    {
        private static string BaseDataPath => Path.Combine(Globals.UserDataFolder, "LoginCache", "Epic_Games");
        private static string AccountsJsonPath => Path.Combine(BaseDataPath, "accounts.json");

        private static List<string> RegistryKeysToSwap => AppSettings.Instance.RegistryKeysToSwap;

        public static List<Account> GetAccounts()
        {
            if (!File.Exists(AccountsJsonPath)) return new List<Account>();
            try
            {
                return JsonConvert.DeserializeObject<List<Account>>(File.ReadAllText(AccountsJsonPath)) ?? new List<Account>();
            }
            catch (Exception ex) 
            { 
                Globals.WriteToLog("Failed to load accounts", ex); 
                return new List<Account>();
            }
        }

        public static void SaveAccounts(List<Account> accounts)
        {
            Directory.CreateDirectory(BaseDataPath);
            File.WriteAllText(AccountsJsonPath, JsonConvert.SerializeObject(accounts, Formatting.Indented));
        }

        private static async Task<bool> EnsureEpicIsClosed(int timeoutMs = 30000)
        {
            var sw = Stopwatch.StartNew();
            string[] processesToKill = { "EpicGamesLauncher", "EpicWebHelper", "UnrealEditor", "EpicGamesLauncherService" };
            
            Globals.WriteToLog("[KILL] Initiating aggressive process termination...");

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                bool anyRunning = false;
                foreach (var name in processesToKill)
                {
                    if (Globals.IsProcessRunning(name))
                    {
                        anyRunning = true;
                        Globals.KillProcess(name, true);
                    }
                }
                
                if (!anyRunning) 
                {
                    // Verify file locks are released by trying to briefly open a key file for exclusive access
                    var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    var loginCache = Path.Combine(localAppData, "EpicGamesLauncher", "Saved", "LoginCache.json");
                    
                    if (!File.Exists(loginCache)) return true; // Nothing to lock

                    try
                    {
                        using var fs = new FileStream(loginCache, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                        Globals.WriteToLog("[OK] All Epic processes closed and file handles released.");
                        return true;
                    }
                    catch (IOException)
                    {
                        Globals.WriteToLog("[WAIT] Processes closed but file handles still locked. Retrying...");
                    }
                }
                await Task.Delay(1000);
            }
            return false;
        }

        private static async Task<bool> SafeDirectoryDeleteAsync(string path, int retries = 15)
        {
            if (!Directory.Exists(path)) return true;
            for (int i = 0; i < retries; i++)
            {
                try 
                { 
                    Globals.ForceDeleteDirectory(path);
                    return true; 
                }
                catch { await Task.Delay(1000); }
            }
            return false;
        }

        private static (string Path, string Name) GetEpicWebCacheInfo()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var epicSavedPath = Path.Combine(localAppData, "EpicGamesLauncher", "Saved");
            if (!Directory.Exists(epicSavedPath)) return (null, "webcache");

            // 1. Try standard path first
            var standardPath = Path.Combine(epicSavedPath, "webcache");
            if (Directory.Exists(standardPath)) return (standardPath, "webcache");

            // 2. Try variants and pick the most recently used/modified one
            var folders = Directory.GetDirectories(epicSavedPath, "webcache*");
            var best = folders
                .OrderByDescending(f => Directory.GetLastWriteTime(f))
                .FirstOrDefault();

            if (best != null) return (best, Path.GetFileName(best));
            return (null, "webcache");
        }

        public static string GetCurrentAccountId()
        {
            try
            {
                if (Globals.TryReadRegistryKey(@"HKCU\Software\Epic Games\Unreal Engine\Identifiers:AccountId", out var regId))
                    return regId?.ToString();
            }
            catch { }
            return null;
        }

        public static async Task<bool> SwitchToAccount(string accId, Action<string> progress = null)
        {
            var accounts = GetAccounts();
            var target = accounts.FirstOrDefault(a => a.Id == accId);
            if (target == null) 
            {
                Globals.WriteToLog($"[ERROR] Switch aborted: Account ID {accId} not found in accounts.json.");
                return false;
            }

            Globals.WriteToLog($"[START] Switching to account: {target.Name} (ID: {target.Id})");
            progress?.Invoke("Closing Epic Games Launcher...");

            if (!await EnsureEpicIsClosed())
            {
                Globals.WriteToLog("[ERROR] Failed to close Epic Games Launcher within timeout. Processes may be locked.");
                return false;
            }
            Globals.WriteToLog("[OK] All Epic processes closed.");

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var epicSavedPath = Path.Combine(localAppData, "EpicGamesLauncher", "Saved");
            
            // Delete ALL webcache folders to prevent version mismatch confusion
            progress?.Invoke("Clearing current web cache variants...");
            Globals.WriteToLog($"[FS] Scanning for webcache folders in: {epicSavedPath}");
            var currentWebCaches = Directory.GetDirectories(epicSavedPath, "webcache*");
            foreach (var cachePath in currentWebCaches)
            {
                Globals.WriteToLog($"[FS] Deleting webcache variant: {cachePath}");
                if (!await SafeDirectoryDeleteAsync(cachePath))
                {
                    Globals.WriteToLog($"[ERROR] Failed to delete webcache folder at {cachePath}. It might be locked by another process.");
                    return false;
                }
            }

            var savedAccountFolder = Path.Combine(BaseDataPath, target.Id);
            Globals.WriteToLog($"[FS] Source data folder: {savedAccountFolder}");
            
            if (Directory.Exists(savedAccountFolder))
            {
                // Restore LoginCache.json (CRITICAL for authentication)
                var savedLoginCache = Path.Combine(savedAccountFolder, "LoginCache.json");
                if (File.Exists(savedLoginCache))
                {
                    progress?.Invoke("Restoring login session tokens...");
                    var targetLoginCache = Path.Combine(epicSavedPath, "..", "LoginCache.json");
                    Globals.WriteToLog($"[FS] Restoring LoginCache.json to: {targetLoginCache}");
                    Globals.CopyFile(savedLoginCache, targetLoginCache, true);
                }

                // Restore Common folder
                var savedCommon = Path.Combine(savedAccountFolder, "Common");
                if (Directory.Exists(savedCommon))
                {
                    progress?.Invoke("Restoring common launcher data...");
                    var targetCommonPath = Path.Combine(epicSavedPath, "Common");
                    Globals.WriteToLog($"[FS] Restoring Common to: {targetCommonPath}");
                    await Globals.CopyFilesRecursiveAsync(savedCommon, targetCommonPath);
                }

                // Restore webcache (to the specific source path name)
                var savedWebCache = Path.Combine(savedAccountFolder, "webcache");
                if (Directory.Exists(savedWebCache))
                {
                    progress?.Invoke("Restoring account web cache...");
                    string targetCacheName = !string.IsNullOrEmpty(target.SourceWebCacheName) ? target.SourceWebCacheName : "webcache";
                    var targetWebPath = Path.Combine(epicSavedPath, targetCacheName);
                    Globals.WriteToLog($"[FS] Restoring webcache to: {targetWebPath}");
                    await Globals.CopyFilesRecursiveAsync(savedWebCache, targetWebPath);
                }

                // Restore Config
                var savedConfig = Path.Combine(savedAccountFolder, "Config");
                if (Directory.Exists(savedConfig))
                {
                    progress?.Invoke("Restoring account configuration...");
                    var targetConfigPath = Path.Combine(epicSavedPath, "Config");
                    Globals.WriteToLog($"[FS] Restoring Config to: {targetConfigPath}");
                    await Globals.CopyFilesRecursiveAsync(savedConfig, targetConfigPath);
                }

                // Restore Data folder
                var savedData = Path.Combine(savedAccountFolder, "Data");
                if (Directory.Exists(savedData))
                {
                    progress?.Invoke("Restoring account metadata...");
                    var targetDataPath = Path.Combine(epicSavedPath, "Data");
                    Globals.WriteToLog($"[FS] Restoring Data to: {targetDataPath}");
                    await Globals.CopyFilesRecursiveAsync(savedData, targetDataPath);
                }

                // Restore UserVaultSettings
                var savedVault = Path.Combine(savedAccountFolder, "UserVaultSettings");
                if (Directory.Exists(savedVault))
                {
                    progress?.Invoke("Restoring account vault settings...");
                    var targetVaultPath = Path.Combine(epicSavedPath, "UserVaultSettings");
                    Globals.WriteToLog($"[FS] Restoring UserVaultSettings to: {targetVaultPath}");
                    await Globals.CopyFilesRecursiveAsync(savedVault, targetVaultPath);
                }
            }
            else
            {
                Globals.WriteToLog($"[WARNING] No saved data folder found for account {target.Id}. Only registry will be applied.");
            }

            if (target.RegistryData != null)
            {
                progress?.Invoke("Applying registry settings...");
                foreach (var kvp in target.RegistryData)
                {
                    Globals.WriteToLog($"[REG] Setting key: {kvp.Key}");
                    Globals.SetRegistryKey(kvp.Key, kvp.Value);
                }
            }

            progress?.Invoke("Launching Epic Games...");
            Globals.WriteToLog("[CMD] Starting Epic Games Launcher via URI protocol.");
            Process.Start(new ProcessStartInfo
            {
                FileName = "com.epicgames.launcher://",
                UseShellExecute = true
            });

            Globals.WriteToLog("[FINISH] Switch successful.");
            return true;
        }

        public static (string Id, string Name, string Email) GetDetectedMetadata()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var epicSavedPath = Path.Combine(localAppData, "EpicGamesLauncher", "Saved");
            var epicConfigPath = Path.Combine(epicSavedPath, "Config");

            string detectedId = GetCurrentAccountId();
            string detectedName = "N/A"; 
            string detectedEmail = "N/A";

            try {
                // 1. Try to find DisplayName/Email in GameUserSettings.ini (Recursive search)
                if (Directory.Exists(epicConfigPath))
                {
                    var iniFiles = Directory.GetFiles(epicConfigPath, "GameUserSettings.ini", SearchOption.AllDirectories);
                    foreach (var iniFile in iniFiles)
                    {
                        var lines = File.ReadAllLines(iniFile);
                        foreach (var line in lines)
                        {
                            var trimmed = line.Trim();
                            if (trimmed.StartsWith("LastCheckedEmail=")) detectedEmail = trimmed.Split('=')[1].Trim();
                            if (trimmed.StartsWith("DisplayName=")) detectedName = trimmed.Split('=')[1].Trim();
                        }
                        if (detectedEmail != "N/A" && detectedName != "N/A") break;
                    }
                }

                // 2. Try to find nicknames/emails in Logs (More thorough search)
                var logDir = Path.Combine(localAppData, "EpicGamesLauncher", "Saved", "Logs");
                if (Directory.Exists(logDir))
                {
                    var logFiles = Directory.GetFiles(logDir, "EpicGamesLauncher*.log")
                        .OrderByDescending(f => File.GetLastWriteTime(f))
                        .Take(5); 

                    foreach (var logFile in logFiles)
                    {
                        try {
                            using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            using var reader = new StreamReader(fs);
                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                if (line.Contains("-epicusername=\""))
                                {
                                    var nick = line.Split("-epicusername=\"")[1].Split('\"')[0];
                                    if (!string.IsNullOrEmpty(nick)) detectedName = nick;
                                }
                                if (line.Contains("DisplayName: "))
                                {
                                    var nick = line.Split("DisplayName: ")[1].Trim();
                                    if (!string.IsNullOrEmpty(nick)) detectedName = nick;
                                }
                                if (detectedEmail == "N/A" && line.Contains("@") && (line.Contains("Email") || line.Contains("User")))
                                {
                                    var match = Regex.Match(line, @"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}");
                                    if (match.Success) detectedEmail = match.Value;
                                }
                            }
                        } catch { }
                        if (detectedName != "N/A" && detectedEmail != "N/A") break;
                    }
                }
            } catch (Exception ex) { Globals.WriteToLog("Error gathering metadata", ex); }

            return (detectedId, detectedName, detectedEmail);
        }

        public static async Task<bool> CaptureCurrentAccount(string nameOverride = null, string emailOverride = null, string imagePathOverride = null, string descriptionOverride = null, Action<string> progress = null, Func<string, Task<bool>> confirmOverwrite = null)
        {
            Globals.WriteToLog($"[START] Capturing current account (NameOverride: {nameOverride ?? "None"})");
            
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var epicSavedPath = Path.Combine(localAppData, "EpicGamesLauncher", "Saved");
            var epicConfigPath = Path.Combine(epicSavedPath, "Config");

            var detected = GetDetectedMetadata();
            string detectedId = detected.Id;
            string detectedName = detected.Name;
            string detectedEmail = detected.Email;
            
            Globals.WriteToLog($"[METADATA] Detected ID: {detectedId}, Name: {detectedName}, Email: {detectedEmail}");

            if (string.IsNullOrEmpty(detectedId)) 
            {
                Globals.WriteToLog("[ERROR] Capture aborted: Could not detect AccountId from registry.");
                return false;
            }

            var accounts = GetAccounts();
            var existing = accounts.FirstOrDefault(a => a.Id == detectedId);
            
            if (existing != null && nameOverride == null && confirmOverwrite != null)
            {
                Globals.WriteToLog($"[PROMPT] Account {detectedId} already exists as '{existing.Name}'. Waiting for user confirmation.");
                if (!await confirmOverwrite(existing.Name)) 
                {
                    Globals.WriteToLog("[CANCEL] User declined to overwrite existing account.");
                    return false;
                }
            }

            progress?.Invoke("Closing Epic Games Launcher...");
            if (!await EnsureEpicIsClosed())
            {
                Globals.WriteToLog("[ERROR] Failed to close Epic for capture. Files may be locked.");
                return false;
            }

            var webCacheInfo = GetEpicWebCacheInfo();
            if (string.IsNullOrEmpty(webCacheInfo.Path) || !Directory.Exists(webCacheInfo.Path))
            {
                Globals.WriteToLog($"[ERROR] Webcache path not found. Search path: {epicSavedPath}");
                return false;
            }
            Globals.WriteToLog($"[FS] Using webcache: {webCacheInfo.Path}");

            string finalName = nameOverride ?? (detectedName != "N/A" ? detectedName : (existing?.Name ?? detectedName));
            var acc = existing ?? new Account(detectedId, finalName);
            
            acc.SourceWebCacheName = webCacheInfo.Name;
            if (!string.IsNullOrEmpty(nameOverride)) acc.Name = nameOverride;
            else if (detectedName != "N/A") acc.Name = detectedName;

            if (!string.IsNullOrEmpty(emailOverride)) acc.Email = emailOverride;
            else if (detectedEmail != "N/A") acc.Email = detectedEmail;

            if (!string.IsNullOrEmpty(descriptionOverride)) acc.Description = descriptionOverride;
            if (!string.IsNullOrEmpty(imagePathOverride)) acc.ProfileImagePath = imagePathOverride;

            var targetAccountFolder = Path.Combine(BaseDataPath, acc.Id);
            var tempFolder = targetAccountFolder + "_new_tmp";
            var backupFolder = targetAccountFolder + "_old_bak";
            
            progress?.Invoke("Capturing account data safely...");
            Globals.WriteToLog($"[FS] Preparing temp folder: {tempFolder}");
            if (Directory.Exists(tempFolder)) Globals.ForceDeleteDirectory(tempFolder);
            Directory.CreateDirectory(tempFolder);
            
            try 
            {
                // Capture LoginCache.json (CRITICAL for authentication)
                var loginCacheFile = Path.Combine(epicSavedPath, "..", "LoginCache.json");
                if (File.Exists(loginCacheFile))
                {
                    Globals.WriteToLog("[FS] Capturing LoginCache.json...");
                    Globals.CopyFile(loginCacheFile, Path.Combine(tempFolder, "LoginCache.json"), true);
                }

                // Capture webcache (Less aggressive than before)
                var targetWeb = Path.Combine(tempFolder, "webcache");
                var excludeFolders = new List<string> { "Cache", "Code Cache" }; 
                Globals.WriteToLog("[FS] Copying webcache...");
                await Globals.CopyFilesRecursiveAsync(webCacheInfo.Path, targetWeb, true, excludeFolders);

                // Capture Config
                var targetConfig = Path.Combine(tempFolder, "Config");
                if (Directory.Exists(epicConfigPath))
                {
                    Globals.WriteToLog("[FS] Copying Config...");
                    await Globals.CopyFilesRecursiveAsync(epicConfigPath, targetConfig);
                }

                // Capture Common
                var epicCommonPath = Path.Combine(epicSavedPath, "Common");
                if (Directory.Exists(epicCommonPath))
                {
                    Globals.WriteToLog("[FS] Copying Common folder...");
                    await Globals.CopyFilesRecursiveAsync(epicCommonPath, Path.Combine(tempFolder, "Common"));
                }

                // Capture Data (Newer session metadata)
                var epicDataPath = Path.Combine(epicSavedPath, "Data");
                if (Directory.Exists(epicDataPath))
                {
                    Globals.WriteToLog("[FS] Copying Data folder...");
                    await Globals.CopyFilesRecursiveAsync(epicDataPath, Path.Combine(tempFolder, "Data"));
                }

                // Capture UserVaultSettings
                var epicVaultPath = Path.Combine(epicSavedPath, "UserVaultSettings");
                if (Directory.Exists(epicVaultPath))
                {
                    Globals.WriteToLog("[FS] Copying UserVaultSettings...");
                    await Globals.CopyFilesRecursiveAsync(epicVaultPath, Path.Combine(tempFolder, "UserVaultSettings"));
                }

                acc.RegistryData = new Dictionary<string, string>();
                foreach (var key in RegistryKeysToSwap)
                {
                    if (Globals.TryReadRegistryKey(key, out var val))
                    {
                        Globals.WriteToLog($"[REG] Captured key: {key}");
                        acc.RegistryData[key] = val.ToString();
                    }
                }

                acc.AccentColor = GetDeterministicColor(acc.Name);
                if (string.IsNullOrEmpty(acc.ProfileImagePath))
                {
                    acc.ProfileImagePath = "pack://application:,,,/favicon.ico";
                }

                // Safe Swap logic
                progress?.Invoke("Finalizing account save...");
                if (Directory.Exists(targetAccountFolder)) 
                {
                    Globals.WriteToLog("[FS] Backing up existing account folder.");
                    if (Directory.Exists(backupFolder)) Globals.ForceDeleteDirectory(backupFolder);
                    Directory.Move(targetAccountFolder, backupFolder);
                }

                try
                {
                    Globals.WriteToLog("[FS] Committing new account data.");
                    Directory.Move(tempFolder, targetAccountFolder);
                    if (Directory.Exists(backupFolder)) Globals.ForceDeleteDirectory(backupFolder);
                }
                catch
                {
                    Globals.WriteToLog("[ERROR] Failed to commit new data. Rolling back from backup.");
                    if (Directory.Exists(backupFolder) && !Directory.Exists(targetAccountFolder))
                        Directory.Move(backupFolder, targetAccountFolder);
                    throw;
                }

                if (existing == null) accounts.Add(acc);
                SaveAccounts(accounts);
                Globals.WriteToLog("[FINISH] Capture successful.");
                return true;
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("[FATAL] Account capture failed.", ex);
                if (Directory.Exists(tempFolder)) try { Globals.ForceDeleteDirectory(tempFolder); } catch { }
                return false;
            }
        }

        public static void BackupAccounts(string zipPath)
        {
            if (File.Exists(zipPath)) File.Delete(zipPath);
            using var archive = SharpCompress.Archives.Zip.ZipArchive.Create();
            
            // Add accounts.json
            if (File.Exists(AccountsJsonPath)) archive.AddEntry("accounts.json", new FileInfo(AccountsJsonPath));
            
            // Add all account folders
            var accounts = GetAccounts();
            foreach (var acc in accounts)
            {
                var accFolder = Path.Combine(BaseDataPath, acc.Id);
                if (Directory.Exists(accFolder))
                {
                    // Add all files from directory recursively with the account ID as the root folder in the zip
                    foreach (var file in Directory.GetFiles(accFolder, "*", SearchOption.AllDirectories))
                    {
                        var relativePath = Path.Combine(acc.Id, Path.GetRelativePath(accFolder, file));
                        archive.AddEntry(relativePath, new FileInfo(file));
                    }
                }
            }
            
            archive.SaveTo(zipPath, SharpCompress.Common.CompressionType.Deflate);
        }

        public static void RestoreAccounts(string zipPath)
        {
            using (var archive = SharpCompress.Archives.Zip.ZipArchive.Open(zipPath))
            {
                foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                {
                    entry.WriteToDirectory(BaseDataPath, new ExtractionOptions { ExtractFullPath = true, Overwrite = true });
                }
            }
        }

        private static string GetDeterministicColor(string input)
        {
            string[] colors = { "#7AA2F7", "#9ECE6A", "#BB9AF7", "#E0AF68", "#F7768E", "#7DCFFF", "#2AC3DE", "#B4F9F8" };
            int hash = 0;
            foreach (char c in input) hash = (hash << 5) - hash + c;
            return colors[Math.Abs(hash) % colors.Length];
        }

        public static void DeleteAccount(string accId)
        {
            var accounts = GetAccounts();
            var target = accounts.FirstOrDefault(a => a.Id == accId);
            if (target != null)
            {
                accounts.Remove(target);
                SaveAccounts(accounts);
                
                var cachePath = Path.Combine(BaseDataPath, target.Id);
                if (Directory.Exists(cachePath)) Directory.Delete(cachePath, true);
            }
        }
    }
}
