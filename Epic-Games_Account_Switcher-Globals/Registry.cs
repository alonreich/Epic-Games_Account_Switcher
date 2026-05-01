using System;
using Microsoft.Win32;

namespace Epic_Games_Account_Switcher_Globals
{
    public partial class Globals
    {
        public static RegistryKey ExpandRegistryAbbreviation(string abv)
        {
            switch (abv)
            {
                case "HKCR": return Registry.ClassesRoot;
                case "HKCU": return Registry.CurrentUser;
                case "HKLM": return Registry.LocalMachine;
                case "HKCC": return Registry.CurrentConfig;
                case "HKPD": return Registry.PerformanceData;
                default: return null;
            }
        }

        private static Tuple<RegistryKey, string, string> ExplodeRegistryKey(string encodedPath)
        {
            try
            {
                if (string.IsNullOrEmpty(encodedPath) || encodedPath.Length < 5) return new Tuple<RegistryKey, string, string>(null, null, null);
                var rootKey = ExpandRegistryAbbreviation(encodedPath.Substring(0, 4));
                if (rootKey == null) return new Tuple<RegistryKey, string, string>(null, null, null);

                var remaining = encodedPath.Substring(5);
                var parts = remaining.Split(':');
                if (parts.Length < 2) return new Tuple<RegistryKey, string, string>(null, null, null);

                return new Tuple<RegistryKey, string, string>(rootKey, parts[0], parts[1]);
            }
            catch { return new Tuple<RegistryKey, string, string>(null, null, null); }
        }

        public static bool TryReadRegistryKey(string encodedPath, out string result)
        {
            result = null;
            var exploded = ExplodeRegistryKey(encodedPath);
            var rootKey = exploded.Item1;
            var path = exploded.Item2;
            var subKey = exploded.Item3;
            if (rootKey == null || path == null || subKey == null) return false;

            try
            {
                using (var key = rootKey.OpenSubKey(path))
                {
                    if (key == null) return false;
                    if (key.GetValue(subKey) == null) return false;

                    var kind = key.GetValueKind(subKey);
                    var val = key.GetValue(subKey);
                    if (val == null) return false;

                    switch (kind)
                    {
                        case RegistryValueKind.Binary:
                            result = "bin:" + BitConverter.ToString((byte[])val).Replace("-", "");
                            break;
                        case RegistryValueKind.DWord:
                            result = "dw:" + val;
                            break;
                        default:
                            result = "sz:" + val;
                            break;
                    }
                    return true;
                }
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

        public static bool DeleteRegistryValue(string encodedPath)
        {
            var exploded = ExplodeRegistryKey(encodedPath);
            var rootKey = exploded.Item1;
            var path = exploded.Item2;
            var subKey = exploded.Item3;
            if (rootKey == null || path == null || subKey == null) return false;

            try
            {
                using (var key = rootKey.OpenSubKey(path, true))
                {
                    if (key == null) 
                    {
                        Globals.WriteToLog("[REG] Skipping deletion: Key path not found (" + path + ")");
                        return true;
                    }
                    
                    if (key.GetValue(subKey) != null)
                    {
                        key.DeleteValue(subKey, false);
                        Globals.WriteToLog("[REG] Successfully deleted value: " + subKey + " from " + path);
                    }
                    return true;
                }
            }
            catch (Exception e)
            {
                Globals.WriteToLog("[REG] ERROR: Failed to delete value " + subKey + " from " + path, e);
                return false;
            }
        }

        public static bool SetRegistryKey(string encodedPath, string value = "")
        {
            var exploded = ExplodeRegistryKey(encodedPath);
            var rootKey = exploded.Item1;
            var path = exploded.Item2;
            var subKey = exploded.Item3;
            if (rootKey == null || path == null || subKey == null) return false;

            try
            {
                using (var key = rootKey.CreateSubKey(path))
                {
                    if (key == null) 
                    {
                        Globals.WriteToLog("[REG] ERROR: Could not create/open registry path: " + path);
                        return false;
                    }

                    if (string.IsNullOrEmpty(value))
                    {
                        key.SetValue(subKey, "");
                        Globals.WriteToLog("[REG] Set " + subKey + " to empty string.");
                        return true;
                    }

                    if (value.StartsWith("bin:"))
                    {
                        key.SetValue(subKey, StringToByteArray(value.Substring(4)), RegistryValueKind.Binary);
                    }
                    else if (value.StartsWith("dw:"))
                    {
                        int intVal;
                        if (int.TryParse(value.Substring(3), out intVal))
                            key.SetValue(subKey, intVal, RegistryValueKind.DWord);
                    }
                    else if (value.StartsWith("sz:"))
                    {
                        key.SetValue(subKey, value.Substring(3), RegistryValueKind.String);
                    }
                    else
                    {
                        key.SetValue(subKey, value, RegistryValueKind.String);
                    }

                    Globals.WriteToLog("[REG] Successfully restored identity key: " + subKey);
                }
            }
            catch (Exception e)
            {
                Globals.WriteToLog("[REG] ERROR: Failed to set key " + subKey + " at " + path, e);
                return false;
            }

            return true;
        }
    }
}
