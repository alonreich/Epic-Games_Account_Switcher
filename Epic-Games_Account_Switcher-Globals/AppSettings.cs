using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        public bool PromptOnUnsavedLoginProp { get; set; } = true;
        public int WindowWidthProp { get; set; } = 1350;
        public int WindowHeightProp { get; set; } = 750;
        public double WindowLeftProp { get; set; } = -1;
        public double WindowTopProp { get; set; } = -1;

        private static readonly List<string> DefaultRegistryKeys = new()
        {
            @"HKCU\Software\Epic Games\Unreal Engine\Identifiers:AccountId",
            @"HKCU\Software\Epic Games\Unreal Engine\Identifiers:DeviceId",
            @"HKCU\Software\Epic Games\Unreal Engine\Identifiers:EpicAccountId",
            @"HKCU\Software\Epic Games\Unreal Engine\Identifiers:MachineId",
            @"HKCU\Software\Epic Games\Unreal Engine\Identifiers:HardwareId",
            @"HKCU\Software\Epic Games\Unreal Engine\Identifiers:LastStatus",
            @"HKCU\Software\Epic Games\Unreal Engine\Identifiers:LastUser",
            @"HKCU\Software\Epic Games\Unreal Engine\Identifiers:LastUserIdentity",
            @"HKCU\Software\Epic Games\Unreal Engine\Identifiers:LastKnownUser",
            @"HKCU\Software\Epic Games\Unreal Engine\Identifiers:LastCheckedEmail",
            @"HKCU\Software\Epic Games\Unreal Engine\Identifiers:LastUserDisplayName",
            @"HKCU\Software\Epic Games\Unreal Engine\Identifiers:LastUserAccountName",
            @"HKCU\Software\Epic Games\EpicGamesLauncher:LastCheckedEmail",
            @"HKCU\Software\Epic Games\EpicGamesLauncher:LastUser",
            @"HKCU\Software\Epic Games\EpicGamesLauncher:LastStatus",
            @"HKCU\Software\Epic Games\EOS:EpicAccountId",
            @"HKCU\Software\Epic Games\EOS:DeviceId",
            @"HKCU\Software\Epic Games\EOS:HardwareId",
            @"HKCU\Software\Epic Games\EOS:RememberMe",
            @"HKCU\Software\Epic Games\EOS:LastUsedAccount",
            @"HKCU\Software\Epic Games\EOS:LastUsedUser",
            @"HKCU\Software\Epic Games\EOS:LastUsedUserIdentity",
            @"HKCU\Software\Epic Games\EOS:LastUser",
            @"HKCU\Software\Epic Games\EOS:LastUserDisplayName",
            @"HKCU\Software\Epic Games\EOS:LastUserEmail",
            @"HKCU\Software\Epic Games\EOS\Config\User:LastCheckedEmail",
            @"HKCU\Software\Epic Games\EOS\Config\User:DeviceId",
            @"HKCU\Software\Epic Games\EOS\Config\User:EpicAccountId",
            @"HKCU\Software\Epic Games\EOS\Config\User:HardwareId"
        };

        private List<string> _registryKeysToSwap = new();

        public List<string> RegistryKeysToSwap 
        { 
            get 
            {
                var merged = new List<string>(DefaultRegistryKeys);
                if (_registryKeysToSwap != null) merged.AddRange(_registryKeysToSwap);
                return merged.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            set 
            { 
                _registryKeysToSwap = value?.Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>(); 
            } 
        }

        public static bool MinimizeOnSwitch { get => Instance.MinimizeOnSwitchProp; set { lock(_lock) Instance.MinimizeOnSwitchProp = value; } }
        public static bool PromptOnUnsavedLogin { get => Instance.PromptOnUnsavedLoginProp; set { lock(_lock) Instance.PromptOnUnsavedLoginProp = value; } }
        public static int WindowWidth { get => Instance.WindowWidthProp; set { lock(_lock) Instance.WindowWidthProp = value; } }
        public static int WindowHeight { get => Instance.WindowHeightProp; set { lock(_lock) Instance.WindowHeightProp = value; } }
        public static double WindowLeft { get => Instance.WindowLeftProp; set { lock(_lock) Instance.WindowLeftProp = value; } }
        public static double WindowTop { get => Instance.WindowTopProp; set { lock(_lock) Instance.WindowTopProp = value; } }

        public static void LoadSettings()
        {
            lock (_lock)
            {
                _instance = null;
                _ = Instance;
            }
        }

        public static void SaveSettings()
        {
            lock (_lock)
            {
                File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(Instance, Formatting.Indented));
            }
        }
    }
}
