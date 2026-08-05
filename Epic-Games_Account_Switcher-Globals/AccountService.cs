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
    public class AccountService
    {
        public enum BackupProtectionMode
        {
            CurrentUser,
            Password
        }

        public sealed class BackupPasswordRequiredException : Exception
        {
            public BackupPasswordRequiredException() : base("This backup is password-protected. Enter the backup password to continue.") { }
        }

        public sealed class BackupPasswordInvalidException : Exception
        {
            public BackupPasswordInvalidException() : base("The backup password is incorrect or the encrypted backup is damaged.") { }
        }

        private sealed class SessionValidationResult
        {
            public bool IsValid { get; set; }
            public string Message { get; set; }
            public string AccountId { get; set; }
            public DateTime? ExpiresUtc { get; set; }
        }

        private sealed class ModernAuthBlobInfo
        {
            public string AccountId { get; set; }
            public string Path { get; set; }
            public DateTime LastWriteUtc { get; set; }
            public DateTime FreshUntilUtc { get; set; }
        }

        private static string _lastSwitchFailureMessage;
        private static readonly object AccountsFileLock = new object();
        public static readonly SemaphoreSlim SessionOperationLock = new SemaphoreSlim(1, 1);
        private static readonly byte[] EncryptedBackupMagic = Encoding.ASCII.GetBytes("EGASBKP2");
        private const int EncryptedBackupPasswordMode = 1;
        private const int EncryptedBackupCurrentUserMode = 2;
        private const int BackupKeyBytes = 32;
        private const int BackupHmacBytes = 32;
        private const int BackupPasswordIterations = 150000;
        private static string BaseDataPath { get { return Path.Combine(Globals.UserDataFolder, "EG"); } }
        private static string AccountsJsonPath { get { return Path.Combine(BaseDataPath, "accounts.json"); } }
        private static List<string> RegistryKeysToSwap { get { return AppSettings.Instance.RegistryKeysToSwap; } }
        private static string LocalAppData { get { return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } }
        private static string RoamingAppData { get { return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); } }
        private static string CommonAppData { get { return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData); } }
        private static string EpicSavedPath { get { return Path.Combine(LocalAppData, "EpicGamesLauncher", "Saved"); } }
        private static string EpicConfigPath { get { return Path.Combine(EpicSavedPath, "Config"); } }
        private static string ProgramDataManifestsPath { get { return Path.Combine(CommonAppData, "Epic", "EpicGamesLauncher", "Data", "Manifests"); } }

        private static readonly string[] AuthFiles = { "PersistentAuthStorage.json", "Config\\Windows\\PersistentAuthStorage.json" };
        private static readonly string[] AuthBinaryFiles = { "PersistentAuthStorage.bin", "Config\\Windows\\PersistentAuthStorage.bin" };
        private static readonly string[] SessionItemsToCapture = { "Config", "Common", "SocialCache", "WebCaches", "Manifests", "Data", "UserVaultSettings", "PersistentStorage", "Portal", "Overlay", "LoginCache.json", "Engine.ini" };
        private static readonly string[] EOSFoldersToCapture = { "Epic Online Services", "EOSOverlay", "EOSUserHelper", "EpicOnlineServicesUserHelper", "EpicOnlineServices", "EpicGamesLauncher\\Data", "EpicGamesLauncher\\Saved\\SocialCache" };
        private static readonly string[] RegistryRootsToCapture = { @"HKCU\Software\Epic Games\Unreal Engine\Identifiers", @"HKCU\Software\Epic Games\Unreal Engine\EOS", @"HKCU\Software\Epic Games\EOS", @"HKCU\Software\Epic Games\EpicGamesLauncher" };

        public static string LastSwitchFailureMessage { get { return _lastSwitchFailureMessage ?? ""; } }

        public static string GetDefaultProfileImagePath()
        {
            var cands = new[] { Path.Combine(Globals.UserDataFolder, "Skin_Icons", "Default_Skin_Girl.jpg"), Path.Combine(Globals.UserDataFolder, "Skin_Icons", "Default_Skin_Guy.jpg"), Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Skin_Icons", "Default_Skin_Guy.jpg"), Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Skin_Icons", "Lizard_Skin.jpg") };
            foreach (var c in cands) if (File.Exists(c)) return c;
            return cands[0];
        }

        private static string NormalizeProfileImagePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return GetDefaultProfileImagePath();
            try
            {
                var fn = Path.GetFileName(path);
                var candidates = new List<string>();

                if (Path.IsPathRooted(path))
                {
                    candidates.Add(path);
                }
                else
                {
                    candidates.Add(Path.Combine(Globals.UserDataFolder, path));
                    candidates.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path));
                }

                if (!string.IsNullOrWhiteSpace(fn))
                {
                    candidates.Add(Path.Combine(Globals.UserDataFolder, "Skin_Icons", fn));
                    candidates.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Skin_Icons", fn));
                    candidates.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Payload", "Skin_Icons", fn));
                }

                foreach (var candidate in candidates)
                    if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate)) return Path.GetFullPath(candidate);

                var recursive = FindSkinByFileName(fn);
                if (!string.IsNullOrWhiteSpace(recursive)) return recursive;
            }
            catch { }
            return GetDefaultProfileImagePath();
        }

        private static string FindSkinByFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            var roots = new[]
            {
                Path.Combine(Globals.UserDataFolder, "Skin_Icons"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Skin_Icons"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Payload", "Skin_Icons")
            };

            foreach (var root in roots)
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    var match = Directory.GetFiles(root, fileName, SearchOption.AllDirectories).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(match)) return Path.GetFullPath(match);
                }
                catch { }
            }

            return null;
        }

        public static List<Account> GetAccounts()
        {
            lock (AccountsFileLock)
            {
                if (!File.Exists(AccountsJsonPath)) return new List<Account>();
                try
                {
                    var accs = JsonConvert.DeserializeObject<List<Account>>(File.ReadAllText(AccountsJsonPath)) ?? new List<Account>();
                    foreach (var a in accs) NormalizeAccount(a);
                    MarkActiveAccount(accs, GetCurrentAccountId());
                    return accs;
                }
                catch (Exception ex)
                {
                    Globals.WriteToLog("[ACC] Failed to load accounts.json", ex);
                    return new List<Account>();
                }
            }
        }

        public static void SaveAccounts(List<Account> accounts)
        {
            lock (AccountsFileLock)
            {
                Directory.CreateDirectory(BaseDataPath);
                foreach (var a in accounts) NormalizeAccount(a);
                var tmp = AccountsJsonPath + ".tmp_" + Guid.NewGuid().ToString("N");
                File.WriteAllText(tmp, JsonConvert.SerializeObject(accounts, Formatting.Indented));
                AtomicReplaceFile(tmp, AccountsJsonPath);
            }
        }

        private static void NormalizeAccount(Account a)
        {
            if (a == null) return;
            if (a.Name == null) a.Name = "Unknown Account";
            if (a.Email == null) a.Email = "Unknown Email";
            if (a.Description == null) a.Description = string.Empty;
            a.AccentColor = string.IsNullOrWhiteSpace(a.AccentColor) ? "#7AA2F7" : a.AccentColor;
            a.ProfileImagePath = NormalizeProfileImagePath(a.ProfileImagePath);
            if (a.RegistryData == null) a.RegistryData = new Dictionary<string, string>();
            if (a.Credentials == null) a.Credentials = new List<CredentialService.SavedCredential>();
            if (a.RememberMeDataByPath == null) a.RememberMeDataByPath = new Dictionary<string, string>();
            if (a.LauncherWindowSettingsByPath == null) a.LauncherWindowSettingsByPath = new Dictionary<string, string>();
        }

        private static bool IsSessionValid(string folder)
        {
            return ValidateSession(folder, null).IsValid;
        }

        private static SessionValidationResult ValidateSession(string folder, string expectedAccountId)
        {
            var result = new SessionValidationResult { IsValid = false, Message = "No readable auth state was found. [v1.0.1]" };
            try
            {
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                {
                    result.Message = "Session folder does not exist: " + folder + " [v1.0.1]";
                    return result;
                }

                var accountId = expectedAccountId ?? GetCurrentAccountId();
                var hasLegacyJson = AuthFiles.Any(f => File.Exists(Path.Combine(folder, f)));
                var modernBlob = SelectModernAuthBlob(folder, accountId);
                var hasModernBinary = modernBlob != null;

                if (!hasLegacyJson && !hasModernBinary)
                {
                    result.Message = "Authentication state file is missing. (Checked legacy JSON and modern binary blobs for ID: " + (accountId ?? "NULL") + ") [v1.0.1]";
                    return result;
                }

                if (hasModernBinary)
                {
                    if (modernBlob.FreshUntilUtc <= DateTime.UtcNow.AddMinutes(5))
                    {
                        result.Message = "Modern auth blob is stale. Last write: " + modernBlob.LastWriteUtc.ToString("o") + "; freshness window ended: " + modernBlob.FreshUntilUtc.ToString("o") + " [v1.0.1]";
                        return result;
                    }
                    result.AccountId = modernBlob.AccountId;
                    result.ExpiresUtc = modernBlob.FreshUntilUtc;
                }

                var authPaths = AuthFiles.Select(f => Path.Combine(folder, f)).Where(File.Exists).ToList();
                var allAccountIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                DateTime? bestExpiry = null;
                var unreadable = new List<string>();
                foreach (var p in authPaths)
                {
                    try
                    {
                        var json = File.ReadAllText(p);
                        if (string.IsNullOrWhiteSpace(json)) continue;
                        var token = JsonConvert.DeserializeObject<JToken>(json);
                        var expiries = new List<DateTime>();
                        var accountIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        CollectAuthFacts(token, expiries, accountIds);
                        foreach (var id in accountIds) if (!string.IsNullOrWhiteSpace(id)) allAccountIds.Add(id.Trim());
                        foreach (var exp in expiries)
                        {
                            var utc = exp.Kind == DateTimeKind.Utc ? exp : exp.ToUniversalTime();
                            if (!bestExpiry.HasValue || utc > bestExpiry.Value) bestExpiry = utc;
                        }
                    }
                    catch (Exception ex)
                    {
                        unreadable.Add(Path.GetFileName(p) + ": " + ex.Message);
                    }
                }
                if (!bestExpiry.HasValue)
                {
                    if (hasModernBinary)
                    {
                        result.IsValid = true;
                        result.AccountId = modernBlob.AccountId;
                        result.Message = "Modern binary auth blob is fresh until " + modernBlob.FreshUntilUtc.ToString("o") + " [v1.0.1]";
                        return result;
                    }
                    result.Message = "No future auth expiration could be read." + (unreadable.Count > 0 ? " Errors: " + string.Join("; ", unreadable.ToArray()) : "") + " [v1.0.1]";
                    return result;
                }
                result.ExpiresUtc = bestExpiry.Value;
                if (bestExpiry.Value <= DateTime.UtcNow.AddMinutes(5))
                {
                    result.Message = "Auth token expires too soon: " + bestExpiry.Value.ToString("o") + " [v1.0.1]";
                    return result;
                }
                if (!string.IsNullOrWhiteSpace(expectedAccountId) && allAccountIds.Count > 0 && !allAccountIds.Contains(expectedAccountId))
                {
                    result.Message = "Auth blob account id mismatch. Expected " + expectedAccountId + ". Found " + string.Join(",", allAccountIds.ToArray()) + " [v1.0.1]";
                    return result;
                }
                if (hasModernBinary && !string.Equals(modernBlob.AccountId, expectedAccountId ?? modernBlob.AccountId, StringComparison.OrdinalIgnoreCase))
                {
                    result.Message = "Modern auth blob account id mismatch. Expected " + expectedAccountId + ". Found " + modernBlob.AccountId + " [v1.0.1]";
                    return result;
                }
                result.IsValid = true;
                result.AccountId = allAccountIds.FirstOrDefault() ?? (hasModernBinary ? modernBlob.AccountId : null);
                result.Message = "Auth blob is valid until " + bestExpiry.Value.ToString("o") + " [v1.0.1]";
                return result;
            }
            catch (Exception ex)
            {
                result.Message = ex.Message + " [v1.0.1]";
                Globals.WriteToLog("[AUTH-CHECK] Validation error in " + folder, ex);
                return result;
            }
        }

        private static ModernAuthBlobInfo SelectModernAuthBlob(string folder, string expectedAccountId)
        {
            try
            {
                var dataPath = Path.Combine(folder, "Data");
                if (!Directory.Exists(dataPath)) return null;

                var maxAgeDays = Math.Max(1, AppSettings.Instance.ModernAuthBlobMaxAgeDays);
                var blobs = Directory.GetFiles(dataPath, "OC_*.dat", SearchOption.TopDirectoryOnly)
                    .Select(path =>
                    {
                        var id = Path.GetFileNameWithoutExtension(path);
                        if (!string.IsNullOrWhiteSpace(id) && id.StartsWith("OC_", StringComparison.OrdinalIgnoreCase)) id = id.Substring(3);
                        if (string.IsNullOrWhiteSpace(id)) return null;

                        var lastWrite = File.GetLastWriteTimeUtc(path);
                        if (lastWrite <= DateTime.MinValue.AddYears(1)) lastWrite = File.GetCreationTimeUtc(path);

                        return new ModernAuthBlobInfo
                        {
                            AccountId = id.Trim(),
                            Path = path,
                            LastWriteUtc = lastWrite,
                            FreshUntilUtc = lastWrite.AddDays(maxAgeDays)
                        };
                    })
                    .Where(b => b != null)
                    .ToList();

                if (!string.IsNullOrWhiteSpace(expectedAccountId))
                    return blobs.FirstOrDefault(b => string.Equals(b.AccountId, expectedAccountId, StringComparison.OrdinalIgnoreCase));

                return blobs.Count == 1 ? blobs[0] : null;
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("[AUTH-CHECK] Failed to inspect modern auth blobs in " + folder, ex);
                return null;
            }
        }

        private static void CollectAuthFacts(JToken token, List<DateTime> expiries, HashSet<string> accountIds)
        {
            if (token == null) return;
            var prop = token as JProperty;
            if (prop != null)
            {
                var name = prop.Name ?? "";
                var value = prop.Value == null ? null : prop.Value.ToString();
                if (IsAccountIdName(name) && !string.IsNullOrWhiteSpace(value)) accountIds.Add(value.Trim());
                if (IsExpirationName(name))
                {
                    DateTime exp;
                    if (TryParseExpiration(value, out exp)) expiries.Add(exp);
                }
            }
            foreach (var child in token.Children()) CollectAuthFacts(child, expiries, accountIds);
        }

        private static bool IsAccountIdName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            var n = name.Replace("_", "").Replace("-", "").ToLowerInvariant();
            return n == "accountid" || n == "epicaccountid" || n == "userid";
        }

        private static bool IsExpirationName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            var n = name.Replace("_", "").Replace("-", "").ToLowerInvariant();
            return n == "expiresat" || n == "expires" || n == "expiration" || n == "expirationtime" || n == "expireat";
        }

        private static bool TryParseExpiration(string value, out DateTime expirationUtc)
        {
            expirationUtc = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(value)) return false;
            DateTime parsed;
            if (DateTime.TryParse(value, null, DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces, out parsed))
            {
                expirationUtc = parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
                return true;
            }
            long number;
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            {
                try
                {
                    if (number > 1000000000000L) expirationUtc = DateTimeOffset.FromUnixTimeMilliseconds(number).UtcDateTime;
                    else if (number > 1000000000L) expirationUtc = DateTimeOffset.FromUnixTimeSeconds(number).UtcDateTime;
                    return expirationUtc > DateTime.UtcNow.AddYears(-10);
                }
                catch { }
            }
            return false;
        }

        private static Dictionary<string, string> GetRememberMeDataByPath()
        {
            return CaptureIniValuesByPath("RememberMe", "Data");
        }

        private static Dictionary<string, string> GetLauncherWindowSettingsByPath()
        {
            return CaptureIniValuesByPath("WindowSettings", "MainLauncherWindow");
        }

        private static Dictionary<string, string> CaptureIniValuesByPath(string section, string key)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!Directory.Exists(EpicConfigPath)) return result;
                var files = Directory.GetFiles(EpicConfigPath, "GameUserSettings.ini", SearchOption.AllDirectories);
                foreach (var ini in files)
                {
                    var lines = File.ReadAllLines(ini);
                    var val = GetIniValue(lines, section, key);
                    if (!string.IsNullOrWhiteSpace(val)) result[MakeRelativePath(EpicConfigPath, ini)] = val;
                }
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("[INI] Failed to capture " + section + ":" + key, ex);
            }
            return result;
        }

        private static bool SetRememberMeDataSurgically(Account account)
        {
            if (account == null) return true;
            var values = account.RememberMeDataByPath ?? new Dictionary<string, string>();
            if (values.Count == 0 && !string.IsNullOrWhiteSpace(account.RememberMeData))
            {
                values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var ini in GetGameUserSettingsFiles()) values[MakeRelativePath(EpicConfigPath, ini)] = account.RememberMeData;
            }
            if (values.Count == 0) return true;
            foreach (var kv in values)
            {
                var ini = Path.Combine(EpicConfigPath, kv.Key);
                ApplyRememberMeToIni(ini, kv.Value);
            }
            return VerifyRememberMeApplied(account);
        }

        private static bool SetLauncherWindowSettingsSurgically(Account account)
        {
            if (account == null) return true;
            var values = account.LauncherWindowSettingsByPath ?? new Dictionary<string, string>();
            if (values.Count == 0 && !string.IsNullOrWhiteSpace(account.LauncherWindowSettings))
            {
                values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var ini in GetGameUserSettingsFiles()) values[MakeRelativePath(EpicConfigPath, ini)] = account.LauncherWindowSettings;
            }
            foreach (var kv in values)
            {
                var ini = Path.Combine(EpicConfigPath, kv.Key);
                ApplyIniValuesAtomic(ini, "WindowSettings", new Dictionary<string, string> { { "MainLauncherWindow", kv.Value } });
            }
            return true;
        }

        private static void SetEmailSurgically(string email)
        {
            if (string.IsNullOrWhiteSpace(email)) return;
            try
            {
                foreach (var ini in GetGameUserSettingsFiles())
                {
                    ApplyIniValuesAtomic(ini, "", new Dictionary<string, string> { { "LastCheckedEmail", email } });
                }
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("[INI] Failed to set email metadata", ex);
            }
        }

        private static IEnumerable<string> GetGameUserSettingsFiles()
        {
            if (!Directory.Exists(EpicConfigPath)) return new string[0];
            return Directory.GetFiles(EpicConfigPath, "GameUserSettings.ini", SearchOption.AllDirectories);
        }

        private static void ApplyRememberMeToIni(string ini, string data)
        {
            ApplyIniValuesAtomic(ini, "RememberMe", new Dictionary<string, string> { { "Data", data }, { "Enable", "True" } });
        }

        private static void ApplyIniValuesAtomic(string ini, string section, Dictionary<string, string> values)
        {
            if (string.IsNullOrWhiteSpace(ini) || values == null || values.Count == 0) return;
            Directory.CreateDirectory(Path.GetDirectoryName(ini));
            var lines = File.Exists(ini) ? File.ReadAllLines(ini).ToList() : new List<string>();
            foreach (var kv in values) SetIniValue(lines, section, kv.Key, kv.Value);
            AtomicWriteAllLines(ini, lines);
        }

        private static string GetIniValue(IList<string> lines, string section, string key)
        {
            var inSection = string.IsNullOrEmpty(section);
            foreach (var raw in lines)
            {
                var t = raw.Trim();
                if (!string.IsNullOrEmpty(section))
                {
                    if (t.Equals("[" + section + "]", StringComparison.OrdinalIgnoreCase)) { inSection = true; continue; }
                    if (inSection && t.StartsWith("[", StringComparison.Ordinal) && t.EndsWith("]", StringComparison.Ordinal)) break;
                }
                if (inSection && t.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) return t.Substring(key.Length + 1).Trim();
            }
            return null;
        }

        private static void SetIniValue(List<string> lines, string section, string key, string value)
        {
            if (lines == null) return;
            var keyPrefix = key + "=";
            if (string.IsNullOrEmpty(section))
            {
                for (var i = 0; i < lines.Count; i++)
                {
                    if (lines[i].Trim().StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = keyPrefix + value;
                        return;
                    }
                }
                lines.Add(keyPrefix + value);
                return;
            }
            var sectionHeader = "[" + section + "]";
            var sectionIndex = -1;
            var insertIndex = lines.Count;
            for (var i = 0; i < lines.Count; i++)
            {
                var t = lines[i].Trim();
                if (t.Equals(sectionHeader, StringComparison.OrdinalIgnoreCase))
                {
                    sectionIndex = i;
                    insertIndex = i + 1;
                    continue;
                }
                if (sectionIndex != -1 && t.StartsWith("[", StringComparison.Ordinal) && t.EndsWith("]", StringComparison.Ordinal))
                {
                    insertIndex = i;
                    break;
                }
                if (sectionIndex != -1)
                {
                    insertIndex = i + 1;
                    if (t.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = keyPrefix + value;
                        return;
                    }
                }
            }
            if (sectionIndex == -1)
            {
                if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[lines.Count - 1])) lines.Add("");
                lines.Add(sectionHeader);
                lines.Add(keyPrefix + value);
            }
            else
            {
                lines.Insert(insertIndex, keyPrefix + value);
            }
        }

        private static bool VerifyRememberMeApplied(Account account)
        {
            if (account == null) return true;
            var expected = account.RememberMeDataByPath ?? new Dictionary<string, string>();
            if (expected.Count == 0 && !string.IsNullOrWhiteSpace(account.RememberMeData))
            {
                expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var ini in GetGameUserSettingsFiles()) expected[MakeRelativePath(EpicConfigPath, ini)] = account.RememberMeData;
            }
            if (expected.Count == 0) return true;
            foreach (var kv in expected)
            {
                var ini = Path.Combine(EpicConfigPath, kv.Key);
                if (!File.Exists(ini))
                {
                    Globals.WriteToLog("[VERIFY] RememberMe target missing: " + ini);
                    return false;
                }
                var lines = File.ReadAllLines(ini);
                var data = GetIniValue(lines, "RememberMe", "Data");
                var enable = GetIniValue(lines, "RememberMe", "Enable");
                if (!string.Equals(data, kv.Value, StringComparison.Ordinal) || !string.Equals(enable, "True", StringComparison.OrdinalIgnoreCase))
                {
                    Globals.WriteToLog("[VERIFY] RememberMe mismatch in " + ini);
                    return false;
                }
            }
            return true;
        }

        public static string GetCurrentAccountId()
        {
            var registryId = TryGetCurrentAccountIdFromRegistry();
            if (!string.IsNullOrWhiteSpace(registryId)) return registryId;

            var authStateId = TryGetCurrentAccountIdFromLiveAuthState();
            if (!string.IsNullOrWhiteSpace(authStateId)) return authStateId;

            return null;
        }

        private static string TryGetCurrentAccountIdFromRegistry()
        {
            try
            {
                string id;
                if (Globals.TryReadRegistryKey(@"HKCU\Software\Epic Games\Unreal Engine\Identifiers:AccountId", out id)) return NormalizeRegistryIdentityValue(id);
                if (Globals.TryReadRegistryKey(@"HKCU\Software\Epic Games\Unreal Engine\Identifiers:EpicAccountId", out id)) return NormalizeRegistryIdentityValue(id);
            }
            catch { }
            return null;
        }

        private static string NormalizeRegistryIdentityValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var v = value.Trim();
            if (v.StartsWith("sz:", StringComparison.OrdinalIgnoreCase)) v = v.Substring(3);
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }

        private static string TryGetCurrentAccountIdFromLiveAuthState()
        {
            try
            {
                var accountIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var authPath in AuthFiles.Select(f => Path.Combine(EpicSavedPath, f)).Where(File.Exists))
                {
                    try
                    {
                        var json = Globals.ReadAllText(authPath);
                        if (string.IsNullOrWhiteSpace(json)) continue;
                        var token = JsonConvert.DeserializeObject<JToken>(json);
                        CollectAuthFacts(token, new List<DateTime>(), accountIds);
                    }
                    catch { }
                }

                if (accountIds.Count == 1) return accountIds.First();

                var dataPath = Path.Combine(EpicSavedPath, "Data");
                if (!Directory.Exists(dataPath)) return null;

                var binaryIds = Directory.GetFiles(dataPath, "OC_*.dat", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(n => !string.IsNullOrWhiteSpace(n) && n.StartsWith("OC_", StringComparison.OrdinalIgnoreCase) && n.Length > 3)
                    .Select(n => n.Substring(3))
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return binaryIds.Count == 1 ? binaryIds[0] : null;
            }
            catch { return null; }
        }

        private static string GetShortId() { return Guid.NewGuid().ToString("N").Substring(0, 8); }

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
                return await Globals.CopyFilesRecursiveAsync(source, dest, true);
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
                if (string.IsNullOrWhiteSpace(det.Item1)) return new Tuple<bool, string>(false, "No Epic Games account is currently logged in.");
                if (!string.IsNullOrWhiteSpace(expectedAccountId) && !string.Equals(det.Item1, expectedAccountId, StringComparison.OrdinalIgnoreCase))
                    return new Tuple<bool, string>(false, "The current Epic login does not match the selected profile. Expected " + Globals.SafeLogId(expectedAccountId) + " but detected " + Globals.SafeLogId(det.Item1) + ".");
                progress?.Invoke("Closing Epic Games...");
                if (!await EnsureEpicIsClosed(60000, cancellationToken)) return new Tuple<bool, string>(false, "Could not close all Epic Games processes. Please close the Launcher manually and try again.");
                var liveValidation = ValidateSession(EpicSavedPath, det.Item1);
                if (!liveValidation.IsValid) return new Tuple<bool, string>(false, "Current session is invalid: " + liveValidation.Message);
                var accounts = GetAccounts();
                var existing = accounts.FirstOrDefault(a => a.Id == det.Item1);
                if (existing != null && !forceOverwrite)
                {
                    if (confirmOverwrite == null || !await confirmOverwrite(existing.Name)) return new Tuple<bool, string>(false, "Capture cancelled by user.");
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
                progress?.Invoke("Capturing session...");
                var target = Path.Combine(BaseDataPath, acc.Id);
                tmp = target + "_n" + GetShortId();
                await CaptureSessionToFolder(EpicSavedPath, tmp, cancellationToken);
                var capturedValidation = ValidateSession(tmp, acc.Id);
                if (!capturedValidation.IsValid) { await SafeDeleteDirectoryAsync(tmp, 10, cancellationToken); return new Tuple<bool, string>(false, "Failed to verify captured session: " + capturedValidation.Message); }
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
            catch (Exception ex) { Globals.WriteToLog("[CAPTURE] Fatal error", ex); return new Tuple<bool, string>(false, "An unexpected error occurred: " + ex.Message); }
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
            progress?.Invoke("Saving current session...");
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
                progress?.Invoke("Closing Epic Games...");
                if (!await EnsureEpicIsClosed(60000, cancellationToken))
                    return new Tuple<bool, string>(false, "Could not close Epic Games Launcher.");

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

                progress?.Invoke("Processing login...");
                var exe = ResolveEpicLauncherExecutable();
                if (string.IsNullOrEmpty(exe))
                    return new Tuple<bool, string>(false, "Epic Games Launcher executable not found.");

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
                    return new Tuple<bool, string>(false, "Timed out waiting for Epic Games to consume the exchange code.");

                // We can't hold the lock when calling CaptureCurrentAccount because it also waits on it.
                // We'll release the lock early in the finally block.
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("[EXCHANGE] Fatal error during exchange capture", ex);
                return new Tuple<bool, string>(false, "An unexpected error occurred: " + ex.Message);
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
                        progress?.Invoke("Launching Epic Games...");
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
                        progress?.Invoke("Verifying Epic sign-in...");
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
                    SetLastSwitchFailure("Target account is not saved.");
                    LogSwitch(operationId, "FAILED Target account not found.");
                    return false;
                }
                NormalizeAccount(t);
                var src = Path.Combine(BaseDataPath, t.Id);
                LogSwitch(operationId, "Target resolved. Target=" + Globals.SafeLogId(t.Id) + "; SourceState=" + PathStateSummary(src));
                if (!Directory.Exists(src))
                {
                    SetLastSwitchFailure("Saved account session folder is missing.");
                    LogSwitch(operationId, "FAILED Source folder missing. Source=" + src);
                    return false;
                }
                var sourceValidation = ValidateSession(src, t.Id);
                LogSwitch(operationId, "Source validation. " + ValidationSummary(sourceValidation));
                if (!sourceValidation.IsValid)
                {
                    SetLastSwitchFailure("Saved account session is invalid: " + sourceValidation.Message);
                    LogSwitch(operationId, "FAILED Source validation rejected target session.");
                    return false;
                }
                var curB = GetCurrentAccountId();
                LogSwitch(operationId, "Current live account before close=" + Globals.SafeLogId(curB));
                progress?.Invoke("Closing Epic Games...");
                if (!await EnsureEpicIsClosed(60000, cancellationToken))
                {
                    SetLastSwitchFailure("Epic Games Launcher or one of its helper processes could not be closed.");
                    LogSwitch(operationId, "FAILED Epic process cleanup timed out.");
                    return false;
                }
                LogSwitch(operationId, "Epic process cleanup completed. ElapsedMs=" + sw.ElapsedMilliseconds);
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
                    progress?.Invoke("Preparing session...");
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
                    SetLauncherWindowSettingsSurgically(t);
                    LogSwitch(operationId, "Launcher config patched. RememberMePaths=" + (t.RememberMeDataByPath == null ? 0 : t.RememberMeDataByPath.Count) + "; WindowSettingPaths=" + (t.LauncherWindowSettingsByPath == null ? 0 : t.LauncherWindowSettingsByPath.Count));
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
                    progress?.Invoke("Launching Epic Games...");
                    var launchAccepted = AutoLaunchEpicLauncher(false);
                    LogSwitch(operationId, "END SUCCESS. LaunchRequestAccepted=" + launchAccepted + "; ElapsedMs=" + sw.ElapsedMilliseconds);
                    return true;
                }
                catch (OperationCanceledException)
                {
                    SetLastSwitchFailure("Switch was cancelled.");
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
                    SetLastSwitchFailure("Switch failed during restore: " + ex.Message);
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
                SetLastSwitchFailure("Switch failed before restore: " + ex.Message);
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

        private static void RestoreRegistryIdentity(Account target)
        {
            var backup = CaptureRegistryData();
            try
            {
                ClearRegistryIdentity(backup);
                if (target.RegistryData != null)
                {
                    foreach (var kv in target.RegistryData)
                    {
                        if (!Globals.SetRegistryKey(kv.Key, kv.Value))
                            throw new InvalidOperationException("Failed to restore registry identity key: " + kv.Key);
                    }
                }
                if (!string.IsNullOrWhiteSpace(target.Id))
                {
                    if (!Globals.SetRegistryKey(@"HKCU\Software\Epic Games\Unreal Engine\Identifiers:AccountId", "sz:" + target.Id))
                        throw new InvalidOperationException("Failed to set AccountId registry key.");
                    if (target.RegistryData == null || !target.RegistryData.Keys.Any(k => k.EndsWith(":EpicAccountId", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (!Globals.SetRegistryKey(@"HKCU\Software\Epic Games\Unreal Engine\Identifiers:EpicAccountId", "sz:" + target.Id))
                            throw new InvalidOperationException("Failed to set EpicAccountId registry key.");
                    }
                }
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("Registry identity modification failed; rolling back.", ex);
                RestoreRegistrySnapshot(backup);
                throw;
            }
        }

        private static void RestoreRegistrySnapshot(Dictionary<string, string> snapshot)
        {
            ClearRegistryIdentity(CaptureRegistryData());
            if (snapshot == null) return;
            foreach (var kv in snapshot)
            {
                for (int r = 0; r < 3; r++)
                {
                    if (Globals.SetRegistryKey(kv.Key, kv.Value)) break;
                    Thread.Sleep(100);
                }
            }
        }

        private static void ClearRegistryIdentity(Dictionary<string, string> current)
        {
            current = current ?? new Dictionary<string, string>();
            foreach (var key in current.Keys.Concat(RegistryKeysToSwap).Distinct(StringComparer.OrdinalIgnoreCase)) Globals.DeleteRegistryValue(key);
        }

        private static bool VerifyRegistryIdentity(string expectedAccountId)
        {
            if (string.IsNullOrWhiteSpace(expectedAccountId)) return false;
            var current = GetCurrentAccountId();
            return string.Equals(current, expectedAccountId, StringComparison.OrdinalIgnoreCase);
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

        private static void AtomicWriteAllLines(string path, IList<string> lines)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp_" + Guid.NewGuid().ToString("N");
            File.WriteAllLines(tmp, lines.ToArray());
            AtomicReplaceFile(tmp, path);
        }

        private static void AtomicReplaceFile(string tmp, string target)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(target));
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(target))
            {
                var backup = target + ".bak_" + Guid.NewGuid().ToString("N");
                File.Replace(tmp, target, backup, true);
                try { if (File.Exists(backup)) File.Delete(backup); } catch { }
            }
            else { File.Move(tmp, target); }
        }

        private static string MakeRelativePath(string root, string fullPath)
        {
            var rootUri = new Uri(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar);
            var pathUri = new Uri(Path.GetFullPath(fullPath));
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(pathUri).ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        private static readonly object _metadataLock = new object();
        private static Tuple<string, string, string> _lastDetected;

        private static IEnumerable<string> ReadAllLinesSafe(string path)
        {
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    var lines = new List<string>();
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(fs))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null) lines.Add(line);
                    }
                    return lines;
                }
                catch (IOException)
                {
                    try
                    {
                        var tmpCopy = Path.Combine(Path.GetTempPath(), "read_copy_" + Guid.NewGuid().ToString("N") + ".tmp");
                        File.Copy(path, tmpCopy, true);
                        try { return File.ReadAllLines(tmpCopy); }
                        finally { try { File.Delete(tmpCopy); } catch { } }
                    }
                    catch { }
                    Thread.Sleep(100 * (i + 1));
                }
                catch { break; }
            }
            return new string[0];
        }

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

        public static void BackupAccounts(string zip, Action<string> log = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            BackupAccounts(zip, null, BackupProtectionMode.CurrentUser, log, cancellationToken);
        }

        public static void BackupAccounts(string zip, string password, Action<string> log = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            BackupAccounts(zip, password, BackupProtectionMode.Password, log, cancellationToken);
        }

        public static void BackupAccounts(string zip, string password, BackupProtectionMode protectionMode, Action<string> log = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            SessionOperationLock.Wait(cancellationToken);
            var tmp = Path.Combine(Path.GetTempPath(), "e_bak_" + GetShortId());
            var plainZip = Path.Combine(Path.GetTempPath(), "e_bak_zip_" + GetShortId() + ".zip");
            var outputTmp = zip + ".tmp_" + Guid.NewGuid().ToString("N");
            try
            {
                if (protectionMode == BackupProtectionMode.Password && string.IsNullOrWhiteSpace(password))
                    throw new BackupPasswordRequiredException();

                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(BaseDataPath);
                Directory.CreateDirectory(tmp);
                var sPath = Path.Combine(Globals.UserDataFolder, "appsettings.json");
                if (File.Exists(sPath)) File.Copy(sPath, Path.Combine(tmp, "appsettings.json"), true);
                var accs = GetAccounts();
                foreach (var a in accs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var f = Path.Combine(BaseDataPath, a.Id);
                    if (Directory.Exists(f)) Globals.CopyFilesRecursive(f, Path.Combine(tmp, a.Id), true, new System.Collections.Generic.List<string> { "WebCaches" });
                }
                CopySkinDirectoryToBackup(Path.Combine(Globals.UserDataFolder, "Skin_Icons"), tmp);
                CopySkinDirectoryToBackup(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Skin_Icons"), tmp);
                CopySkinDirectoryToBackup(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Payload", "Skin_Icons"), tmp);

                var portableAccounts = CloneAccounts(accs);
                foreach (var account in portableAccounts) account.ProfileImagePath = MakePortableProfileImagePath(account, tmp, log);
                File.WriteAllText(Path.Combine(tmp, "accounts.json"), JsonConvert.SerializeObject(portableAccounts, Formatting.Indented));

                var zipDir = Path.GetDirectoryName(Path.GetFullPath(zip));
                if (!string.IsNullOrWhiteSpace(zipDir)) Directory.CreateDirectory(zipDir);
                if (File.Exists(plainZip)) File.Delete(plainZip);

                using (var archive = ZipFile.Open(plainZip, ZipArchiveMode.Create))
                {
                    foreach (var file in Directory.GetFiles(tmp, "*", SearchOption.AllDirectories))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string rel = file.Substring(tmp.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        var entry = archive.CreateEntry(rel.Replace(Path.DirectorySeparatorChar, '/'), CompressionLevel.Optimal);
                        using (var entryStream = entry.Open())
                        using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            fs.CopyTo(entryStream);
                        }
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                EncryptBackupFile(plainZip, outputTmp, password, protectionMode, cancellationToken);
                AtomicReplaceFile(outputTmp, zip);
            }
            catch { throw; }
            finally
            {
                if (Directory.Exists(tmp)) try { Globals.ForceDeleteDirectory(tmp); } catch { }
                if (File.Exists(plainZip)) try { File.Delete(plainZip); } catch { }
                if (File.Exists(outputTmp)) try { File.Delete(outputTmp); } catch { }
                SessionOperationLock.Release();
            }
        }

        public static void RestoreAccounts(string zip, CancellationToken cancellationToken = default(CancellationToken))
        {
            RestoreAccounts(zip, null, cancellationToken);
        }

        public static void RestoreAccounts(string zip, string password, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!File.Exists(zip)) throw new FileNotFoundException("Backup file not found: " + zip);
            string restoreZip = zip;
            string decryptedZip = null;
            var lockTaken = false;
            var tmp = Path.Combine(Path.GetTempPath(), "e_res_" + GetShortId());
            var rb = BaseDataPath + "_rb";
            var rollbackAvailable = false;
            try
            {
                restoreZip = PrepareBackupForRestore(zip, password, out decryptedZip, cancellationToken);
                SessionOperationLock.Wait(cancellationToken);
                lockTaken = true;
                cancellationToken.ThrowIfCancellationRequested();
                Globals.WriteToLog("[RESTORE] Starting account restore. Zip=" + zip + "; Target=" + BaseDataPath);
                if (Directory.Exists(tmp)) try { Globals.ForceDeleteDirectory(tmp); } catch { }
                Directory.CreateDirectory(tmp);

                var extractedEntryCount = 0;
                try
                {
                    using (var archive = ZipFile.OpenRead(restoreZip))
                    {
                        foreach (var entry in archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            string fullPath = GetSafeExtractionPath(tmp, entry.FullName);
                            string longPath = fullPath.StartsWith(@"\\") ? fullPath : @"\\?\" + fullPath;

                            Directory.CreateDirectory(Path.GetDirectoryName(longPath));
                            using (var stream = entry.Open())
                            using (var fs = new FileStream(longPath, FileMode.Create, FileAccess.Write))
                            {
                                stream.CopyTo(fs);
                            }
                            extractedEntryCount++;
                        }
                    }
                }
                catch (Exception ex) { throw new IOException("Failed to extract backup safely. [LONG_PATH?] Error: " + ex.Message, ex); }
                Globals.WriteToLog("[RESTORE] Backup extracted. Entries=" + extractedEntryCount + "; Temp=" + tmp);

                var aJson = Path.Combine(tmp, "accounts.json");
                if (!File.Exists(aJson)) throw new InvalidDataException("Backup is invalid: accounts.json is missing.");

                RestoreBackupSkins(tmp);

                var accs = JsonConvert.DeserializeObject<List<Account>>(File.ReadAllText(aJson)) ?? new List<Account>();
                Globals.WriteToLog("[RESTORE] Backup accounts parsed. Count=" + accs.Count);
                foreach (var a in accs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    a.ProfileImagePath = ResolveRestoredProfileImagePath(a.ProfileImagePath, tmp);
                    NormalizeAccount(a);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.Exists(rb)) try { Globals.ForceDeleteDirectory(rb); } catch { }
                if (Directory.Exists(BaseDataPath))
                {
                    try
                    {
                        SafeMovePathBlocking(BaseDataPath, rb);
                        rollbackAvailable = Directory.Exists(rb);
                        Globals.WriteToLog("[RESTORE] Existing account data moved to rollback. Rollback=" + rb);
                    }
                    catch (Exception ex)
                    {
                        rollbackAvailable = false;
                        Globals.WriteToLog("[RESTORE] Existing account data could not be moved to rollback. Falling back to in-place overwrite.", ex);
                    }
                }

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Directory.CreateDirectory(BaseDataPath);
                    if (!Globals.CopyFilesRecursive(tmp, BaseDataPath, true))
                        throw new IOException("Failed to copy restored account files into " + BaseDataPath + ".");

                    var sRestored = Path.Combine(tmp, "appsettings.json");
                    if (File.Exists(sRestored)) { File.Copy(sRestored, Path.Combine(Globals.UserDataFolder, "appsettings.json"), true); AppSettings.LoadSettings(); }

                    RestoreBackupSkins(tmp);
                    SaveAccounts(accs);
                    Globals.WriteToLog("[RESTORE] Account restore completed. Count=" + accs.Count + "; RollbackAvailable=" + rollbackAvailable);
                }
                catch (Exception ex)
                {
                    Globals.WriteToLog("[RESTORE] Restore copy/save failed.", ex);
                    if (rollbackAvailable)
                    {
                        if (Directory.Exists(BaseDataPath)) try { Globals.ForceDeleteDirectory(BaseDataPath); } catch { }
                        try
                        {
                            if (Directory.Exists(rb)) SafeMovePathBlocking(rb, BaseDataPath);
                            Globals.WriteToLog("[RESTORE] Rollback restored after restore failure.");
                        }
                        catch (Exception rollbackEx)
                        {
                            Globals.WriteToLog("[RESTORE] Rollback restore failed after restore failure.", rollbackEx);
                        }
                    }
                    else
                    {
                        Globals.WriteToLog("[RESTORE] Rollback was unavailable because the existing account data folder was locked. Original data was left in place where possible.");
                    }
                    throw;
                }
                if (rollbackAvailable && Directory.Exists(rb)) try { Globals.ForceDeleteDirectory(rb); } catch { }
            }
            finally
            {
                if (Directory.Exists(tmp)) try { Globals.ForceDeleteDirectory(tmp); } catch { }
                if (File.Exists(decryptedZip)) try { File.Delete(decryptedZip); } catch { }
                if (lockTaken) SessionOperationLock.Release();
            }
        }

        public static bool IsEncryptedBackup(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (fs.Length < EncryptedBackupMagic.Length) return false;
                    var buffer = new byte[EncryptedBackupMagic.Length];
                    fs.Read(buffer, 0, buffer.Length);
                    return BytesEqual(buffer, EncryptedBackupMagic);
                }
            }
            catch { return false; }
        }

        private static string PrepareBackupForRestore(string path, string password, out string decryptedZip, CancellationToken cancellationToken)
        {
            decryptedZip = null;
            if (!IsEncryptedBackup(path)) return path;

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                decryptedZip = Path.Combine(Path.GetTempPath(), "e_res_zip_" + GetShortId() + ".zip");
                DecryptBackupFile(path, decryptedZip, password, cancellationToken);
                return decryptedZip;
            }
            catch
            {
                if (File.Exists(decryptedZip)) try { File.Delete(decryptedZip); } catch { }
                throw;
            }
        }

        private static void EncryptBackupFile(string plainPath, string destPath, string password, BackupProtectionMode protectionMode, CancellationToken cancellationToken = default(CancellationToken))
        {
            var mode = protectionMode == BackupProtectionMode.Password ? EncryptedBackupPasswordMode : EncryptedBackupCurrentUserMode;
            byte[] salt = new byte[16];
            byte[] protectedKey = new byte[0];
            byte[] keyMaterial = null;
            try
            {
                using (var rng = RandomNumberGenerator.Create())
                {
                    if (mode == EncryptedBackupPasswordMode)
                    {
                        rng.GetBytes(salt);
                        keyMaterial = DeriveBackupKeyMaterial(password, salt);
                    }
                    else
                    {
                        keyMaterial = new byte[BackupKeyBytes + BackupHmacBytes];
                        rng.GetBytes(keyMaterial);
                        salt = new byte[0];
                        protectedKey = ProtectedData.Protect(keyMaterial, null, DataProtectionScope.CurrentUser);
                    }
                }
                var iv = new byte[16];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(iv);

                using (var hmac = new HMACSHA256(keyMaterial.Skip(BackupKeyBytes).Take(BackupHmacBytes).ToArray()))
                using (var outStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    long plainLen = new FileInfo(plainPath).Length;
                    int cipherLen = (int)(((plainLen / 16) + 1) * 16);

                    using (var msHeader = new MemoryStream())
                    using (var writer = new BinaryWriter(msHeader))
                    {
                        writer.Write(EncryptedBackupMagic);
                        writer.Write(mode);
                        writer.Write(BackupPasswordIterations);
                        WriteLengthPrefixed(writer, salt);
                        WriteLengthPrefixed(writer, protectedKey);
                        WriteLengthPrefixed(writer, iv);
                        writer.Write(cipherLen);
                        writer.Flush();

                        var headerBytes = msHeader.ToArray();
                        outStream.Write(headerBytes, 0, headerBytes.Length);
                        hmac.TransformBlock(headerBytes, 0, headerBytes.Length, null, 0);
                    }

                    using (var aes = Aes.Create())
                    {
                        aes.Key = keyMaterial.Take(BackupKeyBytes).ToArray();
                        aes.IV = iv;
                        aes.Mode = CipherMode.CBC;
                        aes.Padding = PaddingMode.PKCS7;

                        using (var plainStream = new FileStream(plainPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var cs = new CryptoStream(plainStream, aes.CreateEncryptor(), CryptoStreamMode.Read))
                        {
                            byte[] buffer = new byte[81920];
                            int read;
                            while ((read = cs.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                outStream.Write(buffer, 0, read);
                                hmac.TransformBlock(buffer, 0, read, null, 0);
                            }
                        }
                    }

                    hmac.TransformFinalBlock(new byte[0], 0, 0);
                    outStream.Write(hmac.Hash, 0, hmac.Hash.Length);
                }
            }
            finally
            {
                if (keyMaterial != null) Array.Clear(keyMaterial, 0, keyMaterial.Length);
            }
        }

        private static void DecryptBackupFile(string encryptedPath, string destPath, string password, CancellationToken cancellationToken = default(CancellationToken))
        {
            byte[] keyMaterial = null;
            try
            {
                using (var fs = new FileStream(encryptedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (fs.Length < EncryptedBackupMagic.Length + BackupHmacBytes)
                        throw new InvalidDataException("Not an encrypted Epic Switcher backup.");

                    byte[] savedHmac = new byte[BackupHmacBytes];
                    fs.Seek(-BackupHmacBytes, SeekOrigin.End);
                    fs.Read(savedHmac, 0, BackupHmacBytes);
                    fs.Seek(0, SeekOrigin.Begin);

                    using (var reader = new BinaryReader(fs, Encoding.UTF8, true))
                    {
                        var magic = reader.ReadBytes(EncryptedBackupMagic.Length);
                        if (!BytesEqual(magic, EncryptedBackupMagic)) throw new InvalidDataException("Not an encrypted Epic Switcher backup.");

                        var mode = reader.ReadInt32();
                        var iterations = reader.ReadInt32();
                        var salt = reader.ReadBytes(reader.ReadInt32());
                        var protectedKey = reader.ReadBytes(reader.ReadInt32());
                        var iv = reader.ReadBytes(reader.ReadInt32());
                        var cipherLen = reader.ReadInt32();

                        long cipherStart = fs.Position;
                        if (cipherLen < 0 || cipherStart + cipherLen > fs.Length - BackupHmacBytes)
                            throw new InvalidDataException("Corrupted backup file cipher length.");

                        if (mode == EncryptedBackupPasswordMode)
                        {
                            if (string.IsNullOrWhiteSpace(password)) throw new BackupPasswordRequiredException();
                            keyMaterial = DeriveBackupKeyMaterial(password, salt, iterations);
                        }
                        else if (mode == EncryptedBackupCurrentUserMode)
                        {
                            keyMaterial = ProtectedData.Unprotect(protectedKey, null, DataProtectionScope.CurrentUser);
                        }
                        else
                        {
                            throw new InvalidDataException("Unsupported encrypted backup mode.");
                        }

                        using (var hmac = new HMACSHA256(keyMaterial.Skip(BackupKeyBytes).Take(BackupHmacBytes).ToArray()))
                        {
                            fs.Seek(0, SeekOrigin.Begin);
                            long bodyBytesToHash = fs.Length - BackupHmacBytes;
                            byte[] hashBuffer = new byte[81920];
                            while (bodyBytesToHash > 0)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                int toRead = (int)Math.Min(bodyBytesToHash, (long)hashBuffer.Length);
                                int r = fs.Read(hashBuffer, 0, toRead);
                                if (r <= 0) break;
                                hmac.TransformBlock(hashBuffer, 0, r, null, 0);
                                bodyBytesToHash -= r;
                            }
                            hmac.TransformFinalBlock(new byte[0], 0, 0);
                            if (!FixedTimeEquals(savedHmac, hmac.Hash)) throw new BackupPasswordInvalidException();
                        }

                        fs.Seek(cipherStart, SeekOrigin.Begin);
                        using (var boundedStream = new BoundedStream(fs, cipherLen))
                        using (var aes = Aes.Create())
                        {
                            aes.Key = keyMaterial.Take(BackupKeyBytes).ToArray();
                            aes.IV = iv;
                            aes.Mode = CipherMode.CBC;
                            aes.Padding = PaddingMode.PKCS7;

                            using (var cs = new CryptoStream(boundedStream, aes.CreateDecryptor(), CryptoStreamMode.Read))
                            using (var outStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                byte[] buffer = new byte[81920];
                                int read;
                                while ((read = cs.Read(buffer, 0, buffer.Length)) > 0)
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    outStream.Write(buffer, 0, read);
                                }
                            }
                        }
                    }
                }
            }
            catch (BackupPasswordRequiredException) { throw; }
            catch (BackupPasswordInvalidException) { throw; }
            catch (CryptographicException) { throw new BackupPasswordInvalidException(); }
            catch (EndOfStreamException) { throw new BackupPasswordInvalidException(); }
            finally
            {
                if (keyMaterial != null) Array.Clear(keyMaterial, 0, keyMaterial.Length);
            }
        }

        private static byte[] EncryptBackupBytes(byte[] plainBytes, string password, BackupProtectionMode protectionMode)
        {
            if (plainBytes == null) throw new ArgumentNullException("plainBytes");
            var tmpPlain = Path.GetTempFileName();
            var tmpEnc = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(tmpPlain, plainBytes);
                EncryptBackupFile(tmpPlain, tmpEnc, password, protectionMode);
                return File.ReadAllBytes(tmpEnc);
            }
            finally
            {
                try { File.Delete(tmpPlain); } catch { }
                try { File.Delete(tmpEnc); } catch { }
            }
        }

        private static byte[] DecryptBackupBytes(byte[] encryptedBytes, string password)
        {
            if (encryptedBytes == null) throw new ArgumentNullException("encryptedBytes");
            var tmpEnc = Path.GetTempFileName();
            var tmpPlain = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(tmpEnc, encryptedBytes);
                DecryptBackupFile(tmpEnc, tmpPlain, password);
                return File.ReadAllBytes(tmpPlain);
            }
            finally
            {
                try { File.Delete(tmpEnc); } catch { }
                try { File.Delete(tmpPlain); } catch { }
            }
        }

        private class BoundedStream : Stream
        {
            private readonly Stream _base;
            private long _remaining;
            public BoundedStream(Stream baseStream, long length) { _base = baseStream; _remaining = length; }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _remaining;
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_remaining <= 0) return 0;
                int toRead = (int)Math.Min(count, _remaining);
                int read = _base.Read(buffer, offset, toRead);
                _remaining -= read;
                return read;
            }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private static byte[] DeriveBackupKeyMaterial(string password, byte[] salt, int iterations = BackupPasswordIterations)
        {
            using (var derive = new Rfc2898DeriveBytes(password ?? "", salt ?? new byte[0], Math.Max(10000, iterations), HashAlgorithmName.SHA256))
            {
                return derive.GetBytes(BackupKeyBytes + BackupHmacBytes);
            }
        }

        private static byte[] BuildEncryptedBackupBody(int mode, byte[] salt, byte[] protectedKey, byte[] iv, byte[] cipherBytes, int iterations = BackupPasswordIterations)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(EncryptedBackupMagic);
                writer.Write(mode);
                writer.Write(iterations);
                WriteLengthPrefixed(writer, salt);
                WriteLengthPrefixed(writer, protectedKey);
                WriteLengthPrefixed(writer, iv);
                WriteLengthPrefixed(writer, cipherBytes);
                writer.Flush();
                return ms.ToArray();
            }
        }

        private static void WriteLengthPrefixed(BinaryWriter writer, byte[] bytes)
        {
            bytes = bytes ?? new byte[0];
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static byte[] ComputeBackupHmac(byte[] hmacKey, byte[] body)
        {
            using (var hmac = new HMACSHA256(hmacKey))
            {
                return hmac.ComputeHash(body);
            }
        }

        private static byte[] EncryptAesCbc(byte[] plainBytes, byte[] key, byte[] iv)
        {
            using (var aes = Aes.Create())
            {
                aes.Key = key;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                using (var encryptor = aes.CreateEncryptor())
                {
                    return encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
                }
            }
        }

        private static byte[] DecryptAesCbc(byte[] cipherBytes, byte[] key, byte[] iv)
        {
            using (var aes = Aes.Create())
            {
                aes.Key = key;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                using (var decryptor = aes.CreateDecryptor())
                {
                    return decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
                }
            }
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            for (var i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }

        private static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            var diff = 0;
            for (var i = 0; i < left.Length; i++) diff |= left[i] ^ right[i];
            return diff == 0;
        }

        private static List<Account> CloneAccounts(List<Account> accounts)
        {
            var clones = new List<Account>();
            foreach (var account in accounts ?? new List<Account>())
            {
                var clone = new Account(account.Id, account.Name)
                {
                    Email = account.Email,
                    Description = account.Description,
                    ProfileImagePath = account.ProfileImagePath,
                    AccentColor = account.AccentColor,
                    IsActive = account.IsActive,
                    RememberMeData = account.RememberMeData,
                    RememberMeDataByPath = account.RememberMeDataByPath == null ? new Dictionary<string, string>() : new Dictionary<string, string>(account.RememberMeDataByPath),
                    LauncherWindowSettings = account.LauncherWindowSettings,
                    LauncherWindowSettingsByPath = account.LauncherWindowSettingsByPath == null ? new Dictionary<string, string>() : new Dictionary<string, string>(account.LauncherWindowSettingsByPath),
                    Credentials = account.Credentials == null ? new List<CredentialService.SavedCredential>() : account.Credentials.Select(c => new CredentialService.SavedCredential
                    {
                        TargetName = c.TargetName,
                        UserName = c.UserName,
                        Blob = c.Blob == null ? null : c.Blob.ToArray(),
                        Persist = c.Persist,
                        Type = c.Type,
                        AccountId = c.AccountId,
                        CapturedAtUtc = c.CapturedAtUtc
                    }).ToList(),
                    RegistryData = account.RegistryData == null ? new Dictionary<string, string>() : new Dictionary<string, string>(account.RegistryData),
                    EncryptedPassword = account.EncryptedPassword,
                    LastPlayed = account.LastPlayed,
                    UsageCount = account.UsageCount
                };
                clones.Add(clone);
            }

            return clones;
        }

        private static void CopySkinDirectoryToBackup(string source, string backupRoot)
        {
            try
            {
                if (!Directory.Exists(source)) return;
                Globals.CopyFilesRecursive(source, Path.Combine(backupRoot, "Skin_Icons"), true);
            }
            catch { }
        }

        private static string MakePortableProfileImagePath(Account account, string backupRoot, Action<string> log)
        {
            var imagePath = account == null ? null : account.ProfileImagePath;
            if (string.IsNullOrWhiteSpace(imagePath)) return imagePath;

            try
            {
                var fileName = Path.GetFileName(imagePath);
                if (string.IsNullOrWhiteSpace(fileName)) return imagePath;

                if (File.Exists(imagePath))
                {
                    var safeId = MakeSafeFileName(string.IsNullOrWhiteSpace(account.Id) ? account.Name : account.Id);
                    var relative = Path.Combine("Skin_Icons", "Selected", safeId + "_" + fileName);
                    var target = Path.Combine(backupRoot, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Copy(imagePath, target, true);
                    return relative;
                }

                if (imagePath.IndexOf("Skin_Icons", StringComparison.OrdinalIgnoreCase) >= 0)
                    return Path.Combine("Skin_Icons", fileName);
            }
            catch (Exception ex)
            {
                log?.Invoke("WARNING: Failed to preserve selected profile image for " + (account == null ? "unknown" : account.Name) + ": " + ex.Message);
            }

            return imagePath;
        }

        private static void RestoreBackupSkins(string extractedRoot)
        {
            try
            {
                var bSkins = Path.Combine(extractedRoot, "Skin_Icons");
                if (!Directory.Exists(bSkins)) return;

                var targetSkins = Path.Combine(Globals.UserDataFolder, "Skin_Icons");
                Directory.CreateDirectory(targetSkins);
                Globals.CopyFilesRecursive(bSkins, targetSkins, true);
            }
            catch { }
        }

        private static string ResolveRestoredProfileImagePath(string imagePath, string extractedRoot)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) return imagePath;

            try
            {
                if (File.Exists(imagePath)) return Path.GetFullPath(imagePath);

                if (!Path.IsPathRooted(imagePath))
                {
                    var restoredRelative = Path.Combine(Globals.UserDataFolder, imagePath);
                    if (File.Exists(restoredRelative)) return Path.GetFullPath(restoredRelative);

                    var extractedRelative = Path.Combine(extractedRoot, imagePath);
                    if (File.Exists(extractedRelative)) return Path.GetFullPath(extractedRelative);
                }

                var fileName = Path.GetFileName(imagePath);
                if (string.IsNullOrWhiteSpace(fileName)) return imagePath;

                var restoredSkinRoot = Path.Combine(Globals.UserDataFolder, "Skin_Icons");
                if (Directory.Exists(restoredSkinRoot))
                {
                    var restoredMatch = Directory.GetFiles(restoredSkinRoot, fileName, SearchOption.AllDirectories).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(restoredMatch)) return Path.GetFullPath(restoredMatch);
                }

                var extractedSkinRoot = Path.Combine(extractedRoot, "Skin_Icons");
                if (Directory.Exists(extractedSkinRoot))
                {
                    var extractedMatch = Directory.GetFiles(extractedSkinRoot, fileName, SearchOption.AllDirectories).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(extractedMatch)) return Path.GetFullPath(extractedMatch);
                }
            }
            catch { }

            return imagePath;
        }

        private static string MakeSafeFileName(string value)
        {
            var safe = string.IsNullOrWhiteSpace(value) ? "account" : value;
            foreach (var c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
            return safe;
        }

        private static string GetSafeExtractionPath(string extractionRoot, string entryKey)
        {
            if (string.IsNullOrWhiteSpace(entryKey)) throw new InvalidDataException("Backup contains an empty entry name.");

            var root = Path.GetFullPath(extractionRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var entryPath = entryKey.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            var fullPath = Path.GetFullPath(Path.Combine(root, entryPath));

            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Backup contains an unsafe path: " + entryKey);

            return fullPath;
        }

        public static void DeleteAccount(string id)
        {
            var accs = GetAccounts();
            var t = accs.FirstOrDefault(a => a.Id == id);
            if (t == null) return;
            accs.Remove(t);
            SaveAccounts(accs);
            var f = Path.Combine(BaseDataPath, t.Id);
            if (Directory.Exists(f)) try { Globals.ForceDeleteDirectory(f); } catch { }
        }

        private static Dictionary<string, string> CaptureRegistryData()
        {
            var data = new Dictionary<string, string>();
            foreach (var r in RegistryRootsToCapture) CaptureRegistryRecursive(r, data);
            return data;
        }

        private static void CaptureRegistryRecursive(string root, Dictionary<string, string> data)
        {
            try
            {
                var exp = ExplodeRegistryKeyForCapture(root + ":D");
                var rKey = exp.Item1;
                var p = exp.Item2;
                if (rKey == null) return;
                using (var key = rKey.OpenSubKey(p))
                {
                    if (key == null) return;
                    foreach (var vName in key.GetValueNames())
                    {
                        string full = root + ":" + vName;
                        string val;
                        if (Globals.TryReadRegistryKey(full, out val)) data[full] = val;
                    }
                    foreach (var sKey in key.GetSubKeyNames()) CaptureRegistryRecursive(root + "\\" + sKey, data);
                }
            }
            catch { }
        }

        private static Tuple<RegistryKey, string, string> ExplodeRegistryKeyForCapture(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || path.Length < 5) return new Tuple<RegistryKey, string, string>(null, null, null);
                var rKey = path.StartsWith("HKCU") ? Microsoft.Win32.Registry.CurrentUser : (path.StartsWith("HKLM") ? Microsoft.Win32.Registry.LocalMachine : null);
                if (rKey == null) return new Tuple<RegistryKey, string, string>(null, null, null);
                var rem = path.Substring(5);
                var parts = rem.Split(':');
                if (parts.Length < 2) return new Tuple<RegistryKey, string, string>(null, null, null);
                return new Tuple<RegistryKey, string, string>(rKey, parts[0], parts[1]);
            }
            catch { return new Tuple<RegistryKey, string, string>(null, null, null); }
        }

        private static string GetDeterministicColor(string input)
        {
            var colors = new[] { "#7AA2F7", "#9ECE6A", "#BB9AF7", "#E0AF68", "#F7768E", "#7DCFFF", "#2AC3DE", "#B4F9F8" };
            var hash = 0;
            foreach (var ch in input ?? string.Empty) hash = (hash << 5) - hash + ch;
            return colors[Math.Abs(hash) % colors.Length];
        }
    }
}
