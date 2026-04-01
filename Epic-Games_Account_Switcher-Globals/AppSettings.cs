using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace Epic_Games_Account_Switcher_Globals
{
    public class AppSettings
    {
        private static AppSettings _instance;
        private static readonly object _lock = new();
        private static string SettingsPath => Path.Combine(Globals.UserDataFolder, "appsettings.json");

        public static AppSettings Instance
        {
            get
            {
                lock (_lock)
                {
                    if (_instance != null) return _instance;
                    if (File.Exists(SettingsPath))
                    {
                        try { _instance = JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings(); }
                        catch { _instance = new AppSettings(); }
                    }
                    else _instance = new AppSettings();
                    return _instance;
                }
            }
        }

        public bool MinimizeOnSwitchProp { get; set; } = true;
        public int WindowWidthProp { get; set; } = 1350;
        public int WindowHeightProp { get; set; } = 750;
        public double WindowLeftProp { get; set; } = -1;
        public double WindowTopProp { get; set; } = -1;

        public List<string> RegistryKeysToSwap { get; set; } = new()
        {
            @"HKCU\Software\Epic Games\Unreal Engine\Identifiers:AccountId",
            @"HKCU\Software\Epic Games\Unreal Engine\Identifiers:LastStatus",
            @"HKCU\Software\Epic Games\Unreal Engine\Identifiers:LastUser",
            @"HKCU\Software\Epic Games\EOS:EpicAccountId",
            @"HKCU\Software\Epic Games\Unreal Engine\Identifiers:LastUserIdentity"
        };

        public static bool MinimizeOnSwitch { get => Instance.MinimizeOnSwitchProp; set { lock(_lock) Instance.MinimizeOnSwitchProp = value; } }
        public static int WindowWidth { get => Instance.WindowWidthProp; set { lock(_lock) Instance.WindowWidthProp = value; } }
        public static int WindowHeight { get => Instance.WindowHeightProp; set { lock(_lock) Instance.WindowHeightProp = value; } }
        public static double WindowLeft { get => Instance.WindowLeftProp; set { lock(_lock) Instance.WindowLeftProp = value; } }
        public static double WindowTop { get => Instance.WindowTopProp; set { lock(_lock) Instance.WindowTopProp = value; } }

        public static void SaveSettings()
        {
            lock (_lock)
            {
                File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(Instance, Formatting.Indented));
            }
        }
    }
}
