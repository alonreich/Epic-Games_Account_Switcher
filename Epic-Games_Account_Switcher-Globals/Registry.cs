using System;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Epic_Games_Account_Switcher_Globals
{
    public partial class Globals
    {
        [SupportedOSPlatform("windows")]
        public static RegistryKey ExpandRegistryAbbreviation(string abv)
        {
            return abv switch
            {
                "HKCR" => Registry.ClassesRoot,
                "HKCU" => Registry.CurrentUser,
                "HKLM" => Registry.LocalMachine,
                "HKCC" => Registry.CurrentConfig,
                "HKPD" => Registry.PerformanceData,
                _ => null
            };
        }

        [SupportedOSPlatform("windows")]
        private static (RegistryKey rootKey, string path, string subKey) ExplodeRegistryKey(string encodedPath)
        {
            try
            {
                if (string.IsNullOrEmpty(encodedPath) || encodedPath.Length < 5) return (null, null, null);
                var rootKey = ExpandRegistryAbbreviation(encodedPath[..4]);
                if (rootKey == null) return (null, null, null);

                var remaining = encodedPath[5..];
                var parts = remaining.Split(':');
                if (parts.Length < 2) return (null, null, null);

                return (rootKey, parts[0], parts[1]);
            }
            catch { return (null, null, null); }
        }

        [SupportedOSPlatform("windows")]
        public static bool TryReadRegistryKey(string encodedPath, out string result)
        {
            result = null;
            var (rootKey, path, subKey) = ExplodeRegistryKey(encodedPath);
            if (rootKey == null || path == null || subKey == null) return false;

            try
            {
                using var key = rootKey.OpenSubKey(path);
                if (key == null) return false;

                // Check if value exists to avoid exception in GetValueKind
                if (key.GetValue(subKey) == null) return false;

                var kind = key.GetValueKind(subKey);
                var val = key.GetValue(subKey);

                if (val == null) return false;

                result = kind switch
                {
                    RegistryValueKind.Binary => "bin:" + BitConverter.ToString((byte[])val).Replace("-", ""),
                    RegistryValueKind.DWord => "dw:" + val,
                    _ => "sz:" + val
                };

                return true;
            }
            catch
            {
                return false;
            }
        }

        public static byte[] StringToByteArray(string hex)
        {
            var numberChars = hex.Length;
            var bytes = new byte[numberChars / 2];
            for (var i = 0; i < numberChars; i += 2)
                bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
            return bytes;
        }

        [SupportedOSPlatform("windows")]
        public static bool DeleteRegistryValue(string encodedPath)
        {
            var (rootKey, path, subKey) = ExplodeRegistryKey(encodedPath);
            if (rootKey == null || path == null || subKey == null) return false;

            try
            {
                using var key = rootKey.OpenSubKey(path, true);
                if (key == null) 
                {
                    Globals.WriteToLog($"[REG] Skipping deletion: Key path not found ({path})");
                    return true;
                }
                
                if (key.GetValue(subKey) != null)
                {
                    key.DeleteValue(subKey, false);
                    Globals.WriteToLog($"[REG] Successfully deleted value: {subKey} from {path}");
                }
                return true;
            }
            catch (Exception e)
            {
                Globals.WriteToLog($"[REG] ERROR: Failed to delete value {subKey} from {path}", e);
                return false;
            }
        }

        [SupportedOSPlatform("windows")]
        public static bool SetRegistryKey(string encodedPath, string value = "")
        {
            var (rootKey, path, subKey) = ExplodeRegistryKey(encodedPath);
            if (rootKey == null || path == null || subKey == null) return false;

            try
            {
                using var key = rootKey.CreateSubKey(path);
                if (key == null) 
                {
                    Globals.WriteToLog($"[REG] ERROR: Could not create/open registry path: {path}");
                    return false;
                }

                if (string.IsNullOrEmpty(value))
                {
                    key.SetValue(subKey, "");
                    Globals.WriteToLog($"[REG] Set {subKey} to empty string.");
                    return true;
                }

                if (value.StartsWith("bin:"))
                {
                    key.SetValue(subKey, StringToByteArray(value[4..]), RegistryValueKind.Binary);
                }
                else if (value.StartsWith("dw:"))
                {
                    if (int.TryParse(value[3..], out int intVal))
                        key.SetValue(subKey, intVal, RegistryValueKind.DWord);
                }
                else if (value.StartsWith("sz:"))
                {
                    key.SetValue(subKey, value[3..], RegistryValueKind.String);
                }
                else
                {
                    key.SetValue(subKey, value, RegistryValueKind.String);
                }

                Globals.WriteToLog($"[REG] Successfully restored identity key: {subKey}");
            }
            catch (Exception e)
            {
                Globals.WriteToLog($"[REG] ERROR: Failed to set key {subKey} at {path}", e);
                return false;
            }

            return true;
        }
    }
}
