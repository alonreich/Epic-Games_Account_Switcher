using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Epic_Games_Account_Switcher_Globals
{
    public static class FortniteSettingsParser
    {
        public static Dictionary<string, string> ExtractSettings(string accountId)
        {
            var settings = new Dictionary<string, string>();
            if (string.IsNullOrWhiteSpace(accountId)) return settings;

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var savFilePath = Path.Combine(localAppData, "FortniteGame", "Saved", "Cloud", accountId, "ClientSettings.sav");
            if (!File.Exists(savFilePath)) return settings;

            try
            {
                byte[] data = File.ReadAllBytes(savFilePath);
                string[] floatKeys = { "MouseSensitivityX", "MouseSensitivityY", "TargetingMultiplier", "GamepadMouseMultiplier", "MusicVolume", "MasterVolume" };
                
                foreach (var key in floatKeys)
                {
                    int idx = IndexOfSequence(data, Encoding.ASCII.GetBytes(key));
                    if (idx != -1)
                    {
                        int searchIdx = idx + key.Length + 5;
                        int floatPropIdx = IndexOfSequence(data, Encoding.ASCII.GetBytes("FloatProperty"), searchIdx, 50);
                        if (floatPropIdx != -1)
                        {
                            int valueOffset = floatPropIdx + "FloatProperty".Length + 13;
                            if (valueOffset + 4 <= data.Length)
                            {
                                float val = BitConverter.ToSingle(data, valueOffset);
                                settings[key] = val.ToString("0.000");
                            }
                        }
                    }
                }
            }
            catch { }
            return settings;
        }

        private static int IndexOfSequence(byte[] data, byte[] pattern, int start = 0, int maxLookahead = -1)
        {
            int limit = maxLookahead == -1 ? data.Length - pattern.Length : Math.Min(data.Length - pattern.Length, start + maxLookahead);
            for (int i = start; i <= limit; i++)
            {
                bool found = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (data[i + j] != pattern[j])
                    {
                        found = false;
                        break;
                    }
                }
                if (found) return i;
            }
            return -1;
        }
    }
}
