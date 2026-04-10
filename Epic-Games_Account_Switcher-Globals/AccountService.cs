using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Compression;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using Microsoft.Win32;

namespace Epic_Games_Account_Switcher_Globals
{
    public class AccountService
    {
        private static readonly object AccountsFileLock = new();
        private static readonly SemaphoreSlim SessionOperationLock = new(1, 1);

        private static string BaseDataPath => Path.Combine(Globals.UserDataFolder, "LoginCache", "Epic_Games");
        private static string AccountsJsonPath => Path.Combine(BaseDataPath, "accounts.json");
        private static List<string> RegistryKeysToSwap => AppSettings.Instance.RegistryKeysToSwap;

        public static string GetDefaultProfileImagePath()
        {
            var candidates = new[]
            {
                Path.Combine(Globals.UserDataFolder, "Skin_Icons", "Default_Skin_Guy.jpg"),
                Path.Combine(AppContext.BaseDirectory, "Skin_Icons", "Default_Skin_Guy.jpg"),
                Path.Combine(AppContext.BaseDirectory, "Skin_Icons", "Lizard_Skin.jpg"),
                Path.Combine(Path.GetFullPath("Payload\\Skin_Icons"), "Default_Skin_Guy.jpg"),
                Path.Combine(Path.GetFullPath("Skin_Icons"), "Default_Skin_Guy.jpg")
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate)) return candidate;
            }

            return candidates[0];
        }

        private static string NormalizeProfileImagePath(string profileImagePath)
        {
            if (string.IsNullOrWhiteSpace(profileImagePath)) return GetDefaultProfileImagePath();

            try
            {
                // 1. If it exists as is, use it
                if (File.Exists(profileImagePath)) return Path.GetFullPath(profileImagePath);
                
                var fileName = Path.GetFileName(profileImagePath);

                // 2. Try UserData (The new permanent home)
                var userCandidate = Path.Combine(Globals.UserDataFolder, "Skin_Icons", fileName);
                if (File.Exists(userCandidate)) return userCandidate;

                // 3. Try AppDir
                var baseCandidate = Path.Combine(AppContext.BaseDirectory, "Skin_Icons", fileName);
                if (File.Exists(baseCandidate)) return baseCandidate;

                // 4. Try legacy relative path check
                if (!Path.IsPathRooted(profileImagePath))
                {
                    var relUserCandidate = Path.Combine(Globals.UserDataFolder, profileImagePath);
                    if (File.Exists(relUserCandidate)) return relUserCandidate;

                    var relBaseCandidate = Path.Combine(AppContext.BaseDirectory, profileImagePath);
                    if (File.Exists(relBaseCandidate)) return relBaseCandidate;
                }
            }
            catch { }

            return GetDefaultProfileImagePath();
        }

        private static readonly string[] SessionFoldersToCapture =
        {
            "Config",
            "Common",
            "SocialCache",
            "WebCaches",
            "Manifests",
            "Data",
            "UserVaultSettings",
            "PersistentStorage",
            "Portal",
            "Overlay",
            "LoginCache.json",
            "Engine.ini",
            "EpicGamesLauncher"
        };

        private static string GetLocalAppDataPath()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        private static class NativeMethods
        {
            [DllImport("shell32.dll")]
            public static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr pszPath);
        }

        private static readonly string[] EOSFoldersToCapture =
        {
            "Epic Online Services",
            "EOSOverlay",
            "EOSUserHelper",
            "EpicOnlineServicesUserHelper",
            "EpicOnlineServices",
            "EpicGamesLauncher\\Data",
            "EpicGamesLauncher\\Saved\\SocialCache"
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
            acc.RegistryData ??= new Dictionary<string, string>();
            acc.Credentials ??= new List<CredentialService.SavedCredential>();
        }

        private static string GetEpicSavedPath()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "EpicGamesLauncher", "Saved");
        }

        private static List<string> GetAllEpicWebCachePaths(string epicSavedPath)
        {
            if (!Directory.Exists(epicSavedPath)) return new List<string>();
            
            // Modern Epic Launcher uses "webcache" (no suffix) as primary
            // We capture anything containing "webcache" or "web_cache" to be safe
            var candidates = Directory.GetDirectories(epicSavedPath, "*web*cache*", SearchOption.AllDirectories)
                .Where(d => !d.Contains("SocialCache", StringComparison.OrdinalIgnoreCase))
                .ToList();
                
            return candidates;
        }

        private static string GetRememberMeDataSurgically()
        {
            try
            {
                var epicSavedPath = GetEpicSavedPath();
                var epicConfigPath = Path.Combine(epicSavedPath, "Config");
                if (!Directory.Exists(epicConfigPath)) return null;

                var iniFiles = Directory.GetFiles(epicConfigPath, "GameUserSettings.ini", SearchOption.AllDirectories);
                foreach (var ini in iniFiles)
                {
                    var lines = File.ReadAllLines(ini);
                    bool inRM = false;
                    foreach (var line in lines)
                    {
                        var trimmed = line.Trim();
                        if (trimmed == "[RememberMe]") inRM = true;
                        else if (trimmed.StartsWith("[")) inRM = false;

                        if (inRM && trimmed.StartsWith("Data=", StringComparison.OrdinalIgnoreCase))
                        {
                            return trimmed.Substring(5).Trim();
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private static void SetRememberMeDataSurgically(string data)
        {
            try
            {
                var epicSavedPath = GetEpicSavedPath();
                var epicConfigPath = Path.Combine(epicSavedPath, "Config");
                if (!Directory.Exists(epicConfigPath)) return;

                var iniFiles = Directory.GetFiles(epicConfigPath, "GameUserSettings.ini", SearchOption.AllDirectories);
                foreach (var ini in iniFiles)
                {
                    var lines = File.ReadAllLines(ini).ToList();
                    int rmIndex = -1;
                    int dataIndex = -1;
                    int enableIndex = -1;

                    for (int i = 0; i < lines.Count; i++)
                    {
                        var trimmed = lines[i].Trim();
                        if (trimmed == "[RememberMe]") rmIndex = i;
                        else if (rmIndex != -1 && (trimmed.StartsWith("Data=", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("Enable=", StringComparison.OrdinalIgnoreCase)))
                        {
                            if (trimmed.StartsWith("Data=", StringComparison.OrdinalIgnoreCase)) dataIndex = i;
                            if (trimmed.StartsWith("Enable=", StringComparison.OrdinalIgnoreCase)) enableIndex = i;
                        }
                        else if (rmIndex != -1 && trimmed.StartsWith("[")) break;
                    }

                    if (string.IsNullOrWhiteSpace(data))
                    {
                        Globals.WriteToLog($"[SWITCH] Clearing RememberMe INI data for this account: {ini}");
                        if (dataIndex != -1) lines[dataIndex] = "Data=";
                        if (enableIndex != -1) lines[enableIndex] = "Enable=False";
                    }
                    else
                    {
                        Globals.WriteToLog($"[SWITCH] Restoring RememberMe token in: {ini}");
                        if (dataIndex != -1)
                        {
                            lines[dataIndex] = "Data=" + data;
                            if (enableIndex != -1) lines[enableIndex] = "Enable=True";
                        }
                        else if (rmIndex != -1)
                        {
                            lines.Insert(rmIndex + 1, "Data=" + data);
                            lines.Insert(rmIndex + 2, "Enable=True");
                        }
                        else
                        {
                            lines.Add("");
                            lines.Add("[RememberMe]");
                            lines.Add("Data=" + data);
                            lines.Add("Enable=True");
                        }
                    }

                    File.WriteAllLines(ini, lines);
                }
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("[ERROR] Failed to set RememberMe data surgically.", ex);
            }
        }

        private static string GetLauncherWindowSettingsSurgically()
        {
            try
            {
                var epicSavedPath = GetEpicSavedPath();
                var epicConfigPath = Path.Combine(epicSavedPath, "Config");
                if (!Directory.Exists(epicConfigPath)) return null;

                var iniFiles = Directory.GetFiles(epicConfigPath, "GameUserSettings.ini", SearchOption.AllDirectories);
                foreach (var ini in iniFiles)
                {
                    var lines = File.ReadAllLines(ini);
                    bool inWS = false;
                    foreach (var line in lines)
                    {
                        var trimmed = line.Trim();
                        if (trimmed == "[WindowSettings]") inWS = true;
                        else if (trimmed.StartsWith("[")) inWS = false;

                        if (inWS && trimmed.StartsWith("MainLauncherWindow=", StringComparison.OrdinalIgnoreCase))
                        {
                            return trimmed.Substring("MainLauncherWindow=".Length).Trim();
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private static void SetLauncherWindowSettingsSurgically(string data)
        {
            if (string.IsNullOrWhiteSpace(data)) return;

            try
            {
                var epicSavedPath = GetEpicSavedPath();
                var epicConfigPath = Path.Combine(epicSavedPath, "Config");
                if (!Directory.Exists(epicConfigPath)) return;

                var iniFiles = Directory.GetFiles(epicConfigPath, "GameUserSettings.ini", SearchOption.AllDirectories);
                foreach (var ini in iniFiles)
                {
                    var lines = File.ReadAllLines(ini).ToList();
                    int wsIndex = -1;
                    int dataIndex = -1;

                    for (int i = 0; i < lines.Count; i++)
                    {
                        var trimmed = lines[i].Trim();
                        if (trimmed == "[WindowSettings]") wsIndex = i;
                        else if (wsIndex != -1 && trimmed.StartsWith("MainLauncherWindow=", StringComparison.OrdinalIgnoreCase))
                        {
                            dataIndex = i;
                            break;
                        }
                        else if (wsIndex != -1 && trimmed.StartsWith("[")) break;
                    }

                    if (dataIndex != -1)
                    {
                        lines[dataIndex] = "MainLauncherWindow=" + data;
                    }
                    else if (wsIndex != -1)
                    {
                        lines.Insert(wsIndex + 1, "MainLauncherWindow=" + data);
                    }
                    else
                    {
                        lines.Add("");
                        lines.Add("[WindowSettings]");
                        lines.Add("MainLauncherWindow=" + data);
                    }

                    File.WriteAllLines(ini, lines);
                }
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("[ERROR] Failed to set Launcher Window Settings surgically.", ex);
            }
        }

        public static string GetCurrentAccountId()
        {
            try
            {
                if (Globals.TryReadRegistryKey(@"HKCU\Software\Epic Games\Unreal Engine\Identifiers:AccountId", out var accountId))
                    return accountId?.ToString()?.Replace("sz:", "");
            }
            catch { }

            return null;
        }

        private static void SetEmailSurgically(string email)
        {
            try
            {
                var epicSavedPath = GetEpicSavedPath();
                var epicConfigPath = Path.Combine(epicSavedPath, "Config");
                if (!Directory.Exists(epicConfigPath)) return;

                var iniFiles = Directory.GetFiles(epicConfigPath, "GameUserSettings.ini", SearchOption.AllDirectories);
                foreach (var ini in iniFiles)
                {
                    var lines = File.ReadAllLines(ini).ToList();
                    bool found = false;
                    string emailToSet = string.IsNullOrWhiteSpace(email) ? "" : email;

                    for (int i = 0; i < lines.Count; i++)
                    {
                        if (lines[i].Trim().StartsWith("LastCheckedEmail=", StringComparison.OrdinalIgnoreCase))
                        {
                            lines[i] = "LastCheckedEmail=" + emailToSet;
                            found = true;
                        }
                    }
                    if (!found)
                    {
                        lines.Add("LastCheckedEmail=" + emailToSet);
                    }
                    File.WriteAllLines(ini, lines);
                }
            }
            catch (Exception ex) { Globals.WriteToLog("[ERROR] SetEmailSurgically failed.", ex); }
        }

        private static async Task<bool> EnsureEpicIsClosed(int timeoutMs = 60000)
        {
            var sw = Stopwatch.StartNew();
            var processesToKill = new[] 
            { 
                "EpicGamesLauncher", "EpicWebHelper", "EpicGamesLauncherService", 
                "EOSUserHelper", "EpicOnlineServicesUserHelper", "EpicOnlineServices", 
                "CrashReportClient", "UnrealCEFSubProcess", "EOSOverlay", "SocialService",
                "EpicOnlineServicesHost", "SocialServiceHelper", "EpicOnlineServicesOverlay",
                "FortniteClient-Win64-Shipping", "UnrealEditor", "Epic Games Launcher"
            };
            bool didKillAny = false;

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                var anyRunning = false;
                foreach (var procName in processesToKill)
                {
                    if (Globals.IsProcessRunning(procName))
                    {
                        anyRunning = true;
                        didKillAny = true;
                        Globals.KillProcess(procName, true);
                    }
                }

                if (!anyRunning)
                {
                    if (didKillAny) await Task.Delay(500); // Only wait briefly if we actually killed something
                    return true;
                }

                await Task.Delay(2000);
            }

            return false;
        }

        private static async Task<bool> SafeDeleteDirectoryAsync(string path, int retries = 10)
        {
            if (!Directory.Exists(path)) return true;
            await Globals.RetryActionAsync(async () => Globals.ForceDeleteDirectory(path), retries, 500);
            return !Directory.Exists(path);
        }

        private static async Task<bool> SafeReplaceDirectoryAsync(string sourceDir, string targetDir)
        {
            if (!Directory.Exists(sourceDir)) return true;
            Globals.WriteToLog($"[FILE] Moving Directory: {sourceDir} -> {targetDir}");

            var tempTarget = targetDir + "_swap_tmp";
            await SafeDeleteDirectoryAsync(tempTarget);

            Directory.CreateDirectory(tempTarget);
            await Globals.RetryAsync(async () => await Globals.CopyFilesRecursiveAsync(sourceDir, tempTarget, true));

            await SafeDeleteDirectoryAsync(targetDir);
            await Globals.RetryActionAsync(async () => 
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetDir) ?? string.Empty);
                Directory.Move(tempTarget, targetDir);
            });
            return true;
        }

        private static async Task SafeReplaceFileAsync(string source, string target)
        {
            if (!File.Exists(source)) return;
            Globals.WriteToLog($"[FILE] Moving File: {source} -> {target}");

            await Globals.RetryActionAsync(async () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target) ?? string.Empty);

                var tempTarget = target + ".swap_tmp";
                if (File.Exists(tempTarget)) File.Delete(tempTarget);

                File.Copy(source, tempTarget, true);
                if (File.Exists(target)) File.Delete(target);
                File.Move(tempTarget, target);
            });
        }

        private static async Task CaptureSessionToFolder(string epicSavedPath, string destinationFolder)
        {
            Globals.WriteToLog($"[CAPTURE] Starting deep session capture to {destinationFolder}");
            await SafeDeleteDirectoryAsync(destinationFolder);
            Directory.CreateDirectory(destinationFolder);

            // 1. Capture Essential JSON files from Config/Windows AND root (modern auth home)
            string[] authFiles = { "PersistentAuthStorage.json", "PersistentAuthStorage.bin" };
            
            // Check Config/Windows
            var configWindowsPath = Path.Combine(epicSavedPath, "Config", "Windows");
            foreach (var file in authFiles)
            {
                var source = Path.Combine(configWindowsPath, file);
                if (File.Exists(source))
                {
                    Globals.WriteToLog($"[CAPTURE] Auth token found in Config/Windows: {file}");
                    await SafeReplaceFileAsync(source, Path.Combine(destinationFolder, "Config", "Windows", file));
                }
            }

            // Check Root
            foreach (var file in authFiles)
            {
                var source = Path.Combine(epicSavedPath, file);
                if (File.Exists(source))
                {
                    Globals.WriteToLog($"[CAPTURE] Auth token found in Root: {file}");
                    await SafeReplaceFileAsync(source, Path.Combine(destinationFolder, file));
                }
            }

            // 2. Capture ALL WebCaches surgically
            var webCaches = GetAllEpicWebCachePaths(epicSavedPath);
            foreach (var wc in webCaches)
            {
                var relPath = wc.Substring(epicSavedPath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                await SafeReplaceDirectoryAsync(wc, Path.Combine(destinationFolder, "WebCaches_Deep", relPath));
            }

            // 3. Capture Session Folders
            foreach (var item in SessionFoldersToCapture)
            {
                if (item == "WebCaches" || item == "EpicGamesLauncher") continue; // Handled separately or specifically
                var sourcePath = Path.Combine(epicSavedPath, item);
                var destPath = Path.Combine(destinationFolder, item);
                if (Directory.Exists(sourcePath))
                {
                    Globals.WriteToLog($"[CAPTURE] Folder: {item}");
                    await SafeReplaceDirectoryAsync(sourcePath, destPath);
                }
                else if (File.Exists(sourcePath))
                {
                    Globals.WriteToLog($"[CAPTURE] File: {item}");
                    await SafeReplaceFileAsync(sourcePath, destPath);
                }
            }

            // 4. Capture EOS & Overlay Data (The modern auth home)
            var localAppData = GetLocalAppDataPath();
            var eosDestPath = Path.Combine(destinationFolder, "EOS_Data");
            foreach (var relPath in EOSFoldersToCapture)
            {
                // Try LocalAppData\Epic Games\...
                var sourcePath = Path.Combine(localAppData, "Epic Games", relPath);
                bool isRoot = false;
                if (!Directory.Exists(sourcePath)) 
                {
                    // Try LocalAppData\...
                    sourcePath = Path.Combine(localAppData, relPath);
                    isRoot = true;
                }

                if (Directory.Exists(sourcePath))
                {
                    Globals.WriteToLog($"[CAPTURE] EOS Data: {relPath} ({(isRoot ? "Root" : "Epic Folder")})");
                    var destPath = Path.Combine(eosDestPath, relPath);
                    await SafeReplaceDirectoryAsync(sourcePath, destPath);
                    if (isRoot) File.WriteAllText(destPath + ".is_root", "");
                }
            }

            // 5. Capture Epic-Specific Roaming data
            var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var roamingDestPath = Path.Combine(destinationFolder, "Roaming_Data");
            var sourceRoaming = Path.Combine(roamingAppData, "Epic Games", "EpicGamesLauncher");
            if (Directory.Exists(sourceRoaming))
            {
                Globals.WriteToLog($"[CAPTURE] Roaming: EpicGamesLauncher");
                await SafeReplaceDirectoryAsync(sourceRoaming, Path.Combine(roamingDestPath, "EpicGamesLauncher"));
            }

            Globals.WriteToLog("[CAPTURE] Deep session capture complete.");
        }

        private static Dictionary<string, string> CaptureRegistryData()
        {
            var data = new Dictionary<string, string>();
            Globals.WriteToLog("[REG] Starting recursive registry capture...");

            string[] rootPaths = 
            { 
                @"HKCU\Software\Epic Games\Unreal Engine\Identifiers",
                @"HKCU\Software\Epic Games\Unreal Engine\EOS",
                @"HKCU\Software\Epic Games\EOS",
                @"HKCU\Software\Epic Games\EpicGamesLauncher"
            };

            foreach (var rootPath in rootPaths)
            {
                CaptureRegistryRecursive(rootPath, data);
            }

            Globals.WriteToLog($"[REG] Deep capture complete. Total identity tokens: {data.Count}");
            return data;
        }

        private static void CaptureRegistryRecursive(string rootPath, Dictionary<string, string> data)
        {
            try
            {
                var (rootKey, path, _) = ExplodeRegistryKey(rootPath + ":Dummy");
                if (rootKey == null) return;

                using var key = rootKey.OpenSubKey(path);
                if (key == null) return;

                // Capture values in this key
                foreach (var valueName in key.GetValueNames())
                {
                    string fullPath = $"{rootPath}:{valueName}";
                    if (Globals.TryReadRegistryKey(fullPath, out string val))
                    {
                        data[fullPath] = val;
                    }
                }

                // Recurse into subkeys
                foreach (var subKeyName in key.GetSubKeyNames())
                {
                    CaptureRegistryRecursive($"{rootPath}\\{subKeyName}", data);
                }
            }
            catch { }
        }

        private static (RegistryKey rootKey, string path, string subKey) ExplodeRegistryKey(string encodedPath)
        {
            try
            {
                if (string.IsNullOrEmpty(encodedPath) || encodedPath.Length < 5) return (null, null, null);
                var rootKey = encodedPath.StartsWith("HKCU") ? Microsoft.Win32.Registry.CurrentUser : 
                             (encodedPath.StartsWith("HKLM") ? Microsoft.Win32.Registry.LocalMachine : null);
                
                if (rootKey == null) return (null, null, null);

                var remaining = encodedPath[5..];
                var parts = remaining.Split(':');
                if (parts.Length < 2) return (null, null, null);

                return (rootKey, parts[0], parts[1]);
            }
            catch { return (null, null, null); }
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
            string passwordOverride = null,
            Action<string> progress = null,
            Func<string, Task<bool>> confirmOverwrite = null)
        {
            await SessionOperationLock.WaitAsync();
            try
            {
                Globals.WriteToLog("=== CAPTAIN'S LOG: Starting New Account Capture ===");
                
                // CRITICAL: Clear cache before detection to prevent "Stuck Name" bug
                _lastDetected = (null, "N/A", "N/A");

                var detected = await GetDetectedMetadataAsync();
                if (string.IsNullOrWhiteSpace(detected.Id))
                {
                    Globals.WriteToLog("[CAPTURE] ERROR: Could not detect AccountId. Aborting.");
                    return false;
                }
                Globals.WriteToLog($"[CAPTURE] Detected current identity: {detected.Name} ({detected.Id})");

                progress?.Invoke("Closing Epic Games Launcher...");
                if (!await EnsureEpicIsClosed())
                {
                    Globals.WriteToLog("[CAPTURE] ERROR: Failed to close Epic processes. Files might be locked.");
                    return false;
                }

                var accounts = GetAccounts();
                var existing = accounts.FirstOrDefault(a => a.Id == detected.Id);
                if (existing != null && nameOverride == null && confirmOverwrite != null)
                {
                    if (!await confirmOverwrite(existing.Name))
                    {
                        Globals.WriteToLog("[CAPTURE] User declined to overwrite existing account data.");
                        return false;
                    }
                }

                var epicSavedPath = GetEpicSavedPath();
                var account = existing ?? new Account(detected.Id, nameOverride ?? (detected.Name == "N/A" ? "Epic Account" : detected.Name));

                if (!string.IsNullOrWhiteSpace(nameOverride)) account.Name = nameOverride.Trim();
                else if (!string.IsNullOrWhiteSpace(detected.Name) && detected.Name != "N/A") account.Name = detected.Name;

                if (emailOverride != null) account.Email = emailOverride.Trim();
                else if (!string.IsNullOrWhiteSpace(detected.Email) && detected.Email != "N/A") account.Email = detected.Email;

                if (descriptionOverride != null) account.Description = descriptionOverride;
                if (passwordOverride != null) account.Password = passwordOverride;
                if (!string.IsNullOrWhiteSpace(imagePathOverride)) account.ProfileImagePath = imagePathOverride;

                account.ProfileImagePath = NormalizeProfileImagePath(account.ProfileImagePath);
                account.AccentColor = GetDeterministicColor(account.Name ?? account.Id);

                progress?.Invoke("Capturing account session...");
                var targetFolder = Path.Combine(BaseDataPath, account.Id);
                var tempFolder = targetFolder + "_new_tmp";

                await CaptureSessionToFolder(epicSavedPath, tempFolder);
                account.RegistryData = CaptureRegistryData();
                account.Credentials = CredentialService.CaptureCredentials();
                account.RememberMeData = GetRememberMeDataSurgically();
                account.LauncherWindowSettings = GetLauncherWindowSettingsSurgically();

                Globals.WriteToLog($"[CAPTURE] Finalizing storage for account: {account.Name}");
                if (Directory.Exists(targetFolder)) await SafeDeleteDirectoryAsync(targetFolder);
                Directory.Move(tempFolder, targetFolder);

                if (existing == null) accounts.Add(account);
                SaveAccounts(accounts);

                Globals.WriteToLog($"=== CAPTAIN'S LOG: Capture Successful for {account.Name} ===");
                return true;
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("=== CAPTAIN'S LOG: FATAL CAPTURE ERROR ===", ex);
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

            Globals.WriteToLog($"[AUTO-SAVE] Refreshing session data for current account: {currentAccount.Name}");
            progress?.Invoke("Saving current account session...");

            currentAccount.RegistryData = CaptureRegistryData();
            currentAccount.Credentials = CredentialService.CaptureCredentials();
            currentAccount.RememberMeData = GetRememberMeDataSurgically();
            currentAccount.LauncherWindowSettings = GetLauncherWindowSettingsSurgically();

            var targetFolder = Path.Combine(BaseDataPath, currentAccount.Id);
            var tempFolder = targetFolder + "_refresh_tmp";

            await CaptureSessionToFolder(epicSavedPath, tempFolder);

            if (Directory.Exists(targetFolder)) await SafeDeleteDirectoryAsync(targetFolder);
            Directory.Move(tempFolder, targetFolder);

            SaveAccounts(accounts);
            Globals.WriteToLog($"[AUTO-SAVE] Refresh complete for {currentAccount.Name}.");
        }

        private static void MarkActiveAccount(List<Account> accounts, string activeId)
        {
            foreach (var acc in accounts)
            {
                acc.IsActive = string.Equals(acc.Id, activeId, StringComparison.OrdinalIgnoreCase);
            }
        }

        public static void AutoLaunchEpicLauncher()
        {
            if (Globals.IsProcessRunning("EpicGamesLauncher")) return;

            try
            {
                var installLocation = "";
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\EpicGamesLauncher"))
                {
                    installLocation = key?.GetValue("InstallLocation")?.ToString();
                }

                if (string.IsNullOrEmpty(installLocation))
                {
                    using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\EpicGamesLauncher"))
                    {
                        installLocation = key?.GetValue("InstallLocation")?.ToString();
                    }
                }

                if (!string.IsNullOrEmpty(installLocation))
                {
                    var exePath = Path.Combine(installLocation, "Launcher", "Portal", "Binaries", "Win32", "EpicGamesLauncher.exe");
                    if (File.Exists(exePath))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = exePath,
                            Arguments = "-minimized -silent",
                            UseShellExecute = true
                        });
                        return;
                    }
                }
                
                Process.Start(new ProcessStartInfo { FileName = "com.epicgames.launcher://", UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("Failed to auto-launch Epic Launcher", ex);
            }
        }

        public static async Task<bool> SwitchToAccount(string accId, Action<string> progress = null)
        {
            await SessionOperationLock.WaitAsync();
            try
            {
                var accounts = GetAccounts();
                var target = accounts.FirstOrDefault(a => a.Id == accId);
                if (target == null) return false;

                Globals.WriteToLog($"=== CAPTAIN'S LOG: Starting Switch to {target.Name} ({target.Id}) ===");

                progress?.Invoke("Closing Epic Games Launcher...");
                if (!await EnsureEpicIsClosed())
                {
                    Globals.WriteToLog("[SWITCH] ERROR: Failed to close Epic processes. Aborting switch.");
                    return false;
                }

                var epicSavedPath = GetEpicSavedPath();
                await RefreshSnapshotOfCurrentSavedAccount(accounts, target.Id, epicSavedPath, progress);

                var sourceFolder = Path.Combine(BaseDataPath, target.Id);
                if (!Directory.Exists(sourceFolder))
                {
                    Globals.WriteToLog($"[SWITCH] ERROR: Saved data not found for {target.Name}. Path: {sourceFolder}");
                    return false;
                }

                var localAppData = GetLocalAppDataPath();
                var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var safetyBackupBase = Path.Combine(BaseDataPath, "_switch_rollback_tmp");

                try
                {
                    progress?.Invoke("Preparing session rollback point...");
                    Globals.WriteToLog("[SWITCH] Stage 1: Creating Rename-First Rollback point...");
                    if (Directory.Exists(safetyBackupBase)) await SafeDeleteDirectoryAsync(safetyBackupBase);
                    Directory.CreateDirectory(safetyBackupBase);

                    // 1. Move current session to temporary rollback location instead of deleting
                    async Task SafeMoveToRollback(string source, string rollbackSubName)
                    {
                        if (!Directory.Exists(source) && !File.Exists(source)) return;
                        var dest = Path.Combine(safetyBackupBase, rollbackSubName);
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        
                        try 
                        { 
                            if (Directory.Exists(source)) Directory.Move(source, dest);
                            else File.Move(source, dest);
                        }
                        catch 
                        { 
                            // Fallback to copy if move fails (e.g. cross-volume or locked)
                            if (Directory.Exists(source)) await Globals.CopyFilesRecursiveAsync(source, dest, true);
                            else File.Copy(source, dest, true);
                            
                            if (Directory.Exists(source)) await SafeDeleteDirectoryAsync(source);
                            else File.Delete(source);
                        }
                    }

                    await SafeMoveToRollback(epicSavedPath, "Saved");
                    foreach (var relPath in EOSFoldersToCapture)
                    {
                        await SafeMoveToRollback(Path.Combine(localAppData, "Epic Games", relPath), "EOS_" + relPath.Replace("\\", "_"));
                        await SafeMoveToRollback(Path.Combine(localAppData, relPath), "EOS_Root_" + relPath.Replace("\\", "_"));
                    }
                    await SafeMoveToRollback(Path.Combine(roamingAppData, "Epic Games", "EpicGamesLauncher"), "Roaming");

                    Directory.CreateDirectory(epicSavedPath);
                    Globals.WriteToLog("[SWITCH] Current session moved to rollback storage.");

                    progress?.Invoke("Restoring login session...");
                    Globals.WriteToLog("[SWITCH] Stage 2: Restoring saved session data...");
                    
                    if (Directory.Exists(sourceFolder))
                    {
                        // NEW: Explicitly purge ANY remaining webcaches that might have been missed by the rollback move
                        // (Epic sometimes locks these, or they are created in sub-sub-folders)
                        var existingWebCaches = GetAllEpicWebCachePaths(epicSavedPath);
                        foreach (var wc in existingWebCaches)
                        {
                            try { Globals.ForceDeleteDirectory(wc); } catch { }
                        }

                        // A. Restore Unififed Launcher Files Pass
                        foreach (var item in SessionFoldersToCapture)
                        {
                            if (item == "WebCaches" || item == "EpicGamesLauncher") continue;
                            var saved = Path.Combine(sourceFolder, item);
                            var targetLoc = Path.Combine(epicSavedPath, item);
                            if (Directory.Exists(saved)) await Globals.CopyFilesRecursiveAsync(saved, targetLoc, true);
                            else if (File.Exists(saved)) File.Copy(saved, targetLoc, true);
                        }

                        // B. Restore Modern Auth Tokens Pass (Explicitly to ensure they override)
                        string[] authFiles = { "PersistentAuthStorage.json", "PersistentAuthStorage.bin" };
                        foreach (var file in authFiles)
                        {
                            var savedFile = Path.Combine(sourceFolder, file);
                            if (File.Exists(savedFile)) File.Copy(savedFile, Path.Combine(epicSavedPath, file), true);
                            
                            var savedConfigWindows = Path.Combine(sourceFolder, "Config", "Windows", file);
                            if (File.Exists(savedConfigWindows))
                            {
                                var targetConfig = Path.Combine(epicSavedPath, "Config", "Windows");
                                Directory.CreateDirectory(targetConfig);
                                File.Copy(savedConfigWindows, Path.Combine(targetConfig, file), true);
                            }
                        }

                        // C. Restore Deep WebCaches
                        var savedWebCacheDeep = Path.Combine(sourceFolder, "WebCaches_Deep");
                        if (Directory.Exists(savedWebCacheDeep))
                        {
                            Globals.WriteToLog("[SWITCH] Restoring Deep WebCaches...");
                            // We need to restore each subfolder in WebCaches_Deep back to its relative position in Saved
                            foreach (var dir in Directory.GetDirectories(savedWebCacheDeep, "*", SearchOption.AllDirectories))
                            {
                                var relPath = dir.Substring(savedWebCacheDeep.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                                if (string.IsNullOrEmpty(relPath)) continue;

                                var targetLoc = Path.Combine(epicSavedPath, relPath);
                                Directory.CreateDirectory(Path.GetDirectoryName(targetLoc) ?? string.Empty);
                                await Globals.CopyFilesRecursiveAsync(dir, targetLoc, true);
                            }
                            
                            // Also copy root files from WebCaches_Deep if any
                            foreach (var file in Directory.GetFiles(savedWebCacheDeep))
                            {
                                var relPath = Path.GetFileName(file);
                                var targetLoc = Path.Combine(epicSavedPath, relPath);
                                File.Copy(file, targetLoc, true);
                            }
                        }

                        // D. Restore EOS Data
                        var eosSavedPath = Path.Combine(sourceFolder, "EOS_Data");
                        if (Directory.Exists(eosSavedPath))
                        {
                            foreach (var relPath in EOSFoldersToCapture)
                            {
                                var saved = Path.Combine(eosSavedPath, relPath);
                                if (!Directory.Exists(saved)) continue;
                                bool isRoot = File.Exists(saved + ".is_root");
                                var targetLoc = isRoot ? Path.Combine(localAppData, relPath) : Path.Combine(localAppData, "Epic Games", relPath);
                                Directory.CreateDirectory(Path.GetDirectoryName(targetLoc));
                                await Globals.CopyFilesRecursiveAsync(saved, targetLoc, true);
                            }
                        }

                        // E. Restore Roaming Data
                        var savedRoamingPath = Path.Combine(sourceFolder, "Roaming_Data");
                        if (Directory.Exists(savedRoamingPath))
                        {
                            foreach (var folder in Directory.GetDirectories(savedRoamingPath))
                            {
                                var targetLoc = Path.Combine(roamingAppData, "Epic Games", Path.GetFileName(folder));
                                Directory.CreateDirectory(Path.GetDirectoryName(targetLoc));
                                await Globals.CopyFilesRecursiveAsync(folder, targetLoc, true);
                            }
                        }
                    }
                    Globals.WriteToLog("[SWITCH] Session files restored successfully.");

                    Globals.WriteToLog("[SWITCH] Stage 3: Applying registry identity...");
                    // CRITICAL FIX: We no longer wipe the entire registry here.
                    // Wiping the entire tree (especially Identifiers and EOS) was deleting machine fingerprints.
                    // Instead, we let SetRegistryKey overwrite values. This solves the "Email Authentication" loop.
                    
                    foreach (var kv in target.RegistryData) Globals.SetRegistryKey(kv.Key, kv.Value);

                    if (target.Credentials?.Count > 0)
                    {
                        CredentialService.RestoreCredentials(target.Credentials);
                    }

                    SetEmailSurgically(target.Email);
                    SetRememberMeDataSurgically(target.RememberMeData);
                    SetLauncherWindowSettingsSurgically(target.LauncherWindowSettings);

                    MarkActiveAccount(accounts, target.Id);
                    SaveAccounts(accounts);

                    // Clean up rollback point only after total success
                    await SafeDeleteDirectoryAsync(safetyBackupBase);

                    Globals.WriteToLog($"=== CAPTAIN'S LOG: Switch to {target.Name} completed successfully! ===");
                    progress?.Invoke("Launching Epic Games...");
                    AutoLaunchEpicLauncher();
                    return true;
                }
                catch (Exception ex)
                {
                    Globals.WriteToLog("=== CAPTAIN'S LOG: CRITICAL SWITCH ERROR. Attempting Rollback... ===", ex);
                    try
                    {
                        // Rollback from the temporary move location
                        async Task RollbackFromTemp(string rollbackSubName, string target)
                        {
                            var source = Path.Combine(safetyBackupBase, rollbackSubName);
                            if (!Directory.Exists(source) && !File.Exists(source)) return;

                            if (Directory.Exists(target)) await SafeDeleteDirectoryAsync(target);
                            else if (File.Exists(target)) File.Delete(target);

                            Directory.CreateDirectory(Path.GetDirectoryName(target));
                            if (Directory.Exists(source)) Directory.Move(source, target);
                            else File.Move(source, target);
                        }

                        await RollbackFromTemp("Saved", epicSavedPath);
                        foreach (var relPath in EOSFoldersToCapture)
                        {
                            await RollbackFromTemp("EOS_" + relPath.Replace("\\", "_"), Path.Combine(localAppData, "Epic Games", relPath));
                            await RollbackFromTemp("EOS_Root_" + relPath.Replace("\\", "_"), Path.Combine(localAppData, relPath));
                        }
                        await RollbackFromTemp("Roaming", Path.Combine(roamingAppData, "Epic Games", "EpicGamesLauncher"));
                        
                        Globals.WriteToLog("[ROLLBACK] Restore complete.");
                    }
                    catch (Exception rex) { Globals.WriteToLog("[ROLLBACK] FATAL: Rollback failed.", rex); }
                    return false;
                }
            }
            finally { SessionOperationLock.Release(); }
        }

        private static (string Id, string Name, string Email) _lastDetected;

        public static async Task<(string Id, string Name, string Email)> GetDetectedMetadataAsync()
        {
            var detectedId = GetCurrentAccountId();
            if (!string.IsNullOrEmpty(detectedId) && _lastDetected.Id == detectedId && _lastDetected.Name != "N/A")
            {
                return _lastDetected;
            }

            var detectedName = "N/A";
            var detectedEmail = "N/A";

            var epicSavedPath = GetEpicSavedPath();
            var epicConfigPath = Path.Combine(epicSavedPath, "Config");

            try
            {
                if (Directory.Exists(epicConfigPath))
                {
                    var iniFiles = Directory.GetFiles(epicConfigPath, "GameUserSettings.ini", SearchOption.AllDirectories);
                    var ini = iniFiles.FirstOrDefault(f => f.Contains("WindowsEditor", StringComparison.OrdinalIgnoreCase)) ?? iniFiles.FirstOrDefault();

                    if (ini != null)
                    {
                        var lines = await File.ReadAllLinesAsync(ini);
                        foreach (var raw in lines)
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
                    }
                }

                if (detectedName == "N/A" || detectedEmail == "N/A")
                {
                    var logsPath = Path.Combine(epicSavedPath, "Logs");
                    if (Directory.Exists(logsPath))
                    {
                        var logs = Directory.GetFiles(logsPath, "EpicGamesLauncher*.log")
                            .OrderByDescending(File.GetLastWriteTimeUtc)
                            .Take(2) // Only check the 2 most recent logs
                            .ToList();

                        foreach (var logFile in logs)
                        {
                            try
                            {
                                using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                                using var reader = new StreamReader(fs);
                                
                                // Only scan the last 1000 lines of the log to prevent hanging on massive files
                                var queue = new Queue<string>(1000);
                                while (!reader.EndOfStream)
                                {
                                    if (queue.Count >= 1000) queue.Dequeue();
                                    queue.Enqueue(await reader.ReadLineAsync() ?? string.Empty);
                                }

                                foreach (var line in queue.Reverse())
                                {
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

                                    if (detectedEmail == "N/A" && line.Contains('@'))
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
            catch (Exception ex) { Globals.WriteToLog("Error gathering account metadata", ex); }

            _lastDetected = (detectedId, detectedName, detectedEmail);
            return _lastDetected;
        }

        public static (string Id, string Name, string Email) GetDetectedMetadata()
        {
            // Sync wrapper for UI compatibility, but ideally call Async
            return Task.Run(async () => await GetDetectedMetadataAsync()).GetAwaiter().GetResult();
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
            Globals.WriteToLog($"[BACKUP] Starting backup to: {zipPath}");
            Directory.CreateDirectory(BaseDataPath);
            var tempFolder = Path.Combine(Path.GetTempPath(), "epic_accounts_backup_" + Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(tempFolder);

                if (File.Exists(AccountsJsonPath))
                {
                    Globals.WriteToLog("[BACKUP] Backing up accounts.json");
                    File.Copy(AccountsJsonPath, Path.Combine(tempFolder, "accounts.json"), true);
                }

                                                                         
                var settingsPath = Path.Combine(Globals.UserDataFolder, "appsettings.json");
                if (File.Exists(settingsPath))
                {
                    Globals.WriteToLog("[BACKUP] Backing up appsettings.json");
                    File.Copy(settingsPath, Path.Combine(tempFolder, "appsettings.json"), true);
                }

                var accounts = GetAccounts();
                Globals.WriteToLog($"[BACKUP] Backing up {accounts.Count} account folders.");
                foreach (var account in accounts)
                {
                    var accountFolder = Path.Combine(BaseDataPath, account.Id);
                    if (!Directory.Exists(accountFolder)) continue;

                    var targetFolder = Path.Combine(tempFolder, account.Id);
                    Directory.CreateDirectory(targetFolder);
                    Globals.CopyFilesRecursive(accountFolder, targetFolder, true);
                }

                // Also backup the Skin_Icons folder if it exists in UserData (primary) or app directory
                var userSkinIcons = Path.Combine(Globals.UserDataFolder, "Skin_Icons");
                var appSkinIcons = Path.Combine(AppContext.BaseDirectory, "Skin_Icons");
                
                string sourceSkins = Directory.Exists(userSkinIcons) ? userSkinIcons : (Directory.Exists(appSkinIcons) ? appSkinIcons : null);

                if (sourceSkins != null)
                {
                    Globals.WriteToLog($"[BACKUP] Backing up skins from: {sourceSkins}");
                    var targetSkins = Path.Combine(tempFolder, "Skin_Icons");
                    Directory.CreateDirectory(targetSkins);
                    Globals.CopyFilesRecursive(sourceSkins, targetSkins, true);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(zipPath) ?? string.Empty);
                if (File.Exists(zipPath)) File.Delete(zipPath);
                ZipFile.CreateFromDirectory(tempFolder, zipPath, CompressionLevel.Optimal, false);
                Globals.WriteToLog("[BACKUP] Backup successfully created.");
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("[ERROR] Backup failed.", ex);
                throw;
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
            Globals.WriteToLog($"[RESTORE] Starting restore from: {zipPath}");

            var restoreTemp = Path.Combine(Path.GetTempPath(), "epic_restore_tmp_" + Guid.NewGuid().ToString("N"));
            var backupFolder = BaseDataPath + "_restore_backup";

            try
            {
                Directory.CreateDirectory(restoreTemp);
                ZipFile.ExtractToDirectory(zipPath, restoreTemp, true);

                var restoredAccountsJson = Path.Combine(restoreTemp, "accounts.json");
                if (!File.Exists(restoredAccountsJson))
                    throw new InvalidDataException("Backup is missing 'accounts.json'. This is not a valid Epic Games Account Switcher backup.");

                                                       
                List<Account> restoredAccounts;
                try
                {
                    var jsonContent = File.ReadAllText(restoredAccountsJson);
                    restoredAccounts = JsonConvert.DeserializeObject<List<Account>>(jsonContent);
                    if (restoredAccounts == null) throw new Exception("Restored accounts list is null.");
                }
                catch (Exception ex)
                {
                    throw new InvalidDataException("The 'accounts.json' file in the backup is corrupted or invalid: " + ex.Message);
                }

                Globals.WriteToLog($"[RESTORE] Found {restoredAccounts.Count} accounts in backup.");
                                                                
                foreach (var acc in restoredAccounts)
                {
                    NormalizeAccount(acc);
                    
                                                                                               
                    if (!string.IsNullOrWhiteSpace(acc.ProfileImagePath) && (acc.ProfileImagePath.Contains("\\Skin_Icons\\") || acc.ProfileImagePath.Contains("\\Skins_Icons\\")))
                    {
                        var fileName = Path.GetFileName(acc.ProfileImagePath);
                        acc.ProfileImagePath = Path.Combine("Skin_Icons", fileName);                                              
                    }
                }

                                                                      
                var restoredSettingsPath = Path.Combine(restoreTemp, "appsettings.json");
                var currentSettingsPath = Path.Combine(Globals.UserDataFolder, "appsettings.json");

                if (Directory.Exists(backupFolder)) Globals.ForceDeleteDirectory(backupFolder);
                if (Directory.Exists(BaseDataPath)) Directory.Move(BaseDataPath, backupFolder);

                try
                {
                    Directory.CreateDirectory(BaseDataPath);
                    Globals.CopyFilesRecursive(restoreTemp, BaseDataPath, true);
                    
                    if (File.Exists(restoredSettingsPath))
                    {
                        Globals.WriteToLog("[RESTORE] Restoring appsettings.json");
                        File.Copy(restoredSettingsPath, currentSettingsPath, true);
                        AppSettings.LoadSettings();
                    }

                    // Restore the Skin_Icons folder BEFORE saving accounts
                    var backupSkins = Path.Combine(restoreTemp, "Skin_Icons");
                    if (Directory.Exists(backupSkins))
                    {
                        Globals.WriteToLog("[RESTORE] Restoring Skin_Icons folder to UserData.");
                        var targetSkins = Path.Combine(Globals.UserDataFolder, "Skin_Icons");
                        try
                        {
                            Directory.CreateDirectory(targetSkins);
                            Globals.CopyFilesRecursive(backupSkins, targetSkins, true);
                        }
                        catch (Exception ex)
                        {
                            Globals.WriteToLog("[ERROR] Failed to restore Skin_Icons.", ex);
                        }
                    }

                    SaveAccounts(restoredAccounts);
                    Globals.WriteToLog("[RESTORE] Restore complete.");
                }
                catch (Exception ex)
                {
                    Globals.WriteToLog("[ERROR] Restore failed.", ex);
                                                         
                    if (Directory.Exists(BaseDataPath)) Globals.ForceDeleteDirectory(BaseDataPath);
                    if (Directory.Exists(backupFolder)) Directory.Move(backupFolder, BaseDataPath);
                    throw;
                }

                if (Directory.Exists(backupFolder)) Globals.ForceDeleteDirectory(backupFolder);
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