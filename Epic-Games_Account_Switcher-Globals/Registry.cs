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
        public static bool TryReadRegistryKey(string encodedPath, out dynamic result)
        {
            result = null;
            var (rootKey, path, subKey) = ExplodeRegistryKey(encodedPath);
            if (rootKey == null || path == null || subKey == null) return false;

            try
            {
                var val = rootKey.OpenSubKey(path)?.GetValue(subKey);
                if (val != null)
                {
                    result = val;
                    return true;
                }
                return false;
            }
            catch (Exception e)
            {
                WriteToLog("TryReadRegistryKey failed", e);
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
        public static bool SetRegistryKey(string encodedPath, string value = "")
        {
            var (rootKey, path, subKey) = ExplodeRegistryKey(encodedPath);
            if (rootKey == null || path == null || subKey == null) return false;

            try
            {
                using var key = rootKey.CreateSubKey(path);
                if (value.StartsWith("(hex)"))
                {
                    value = value[6..];
                    key?.SetValue(subKey, StringToByteArray(value));
                }
                else
                    key?.SetValue(subKey, value);
            }
            catch (Exception e)
            {
                WriteToLog("SetRegistryKey failed", e);
                return false;
            }

            return true;
        }
    }
}
