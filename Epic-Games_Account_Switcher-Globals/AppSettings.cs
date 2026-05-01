using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Epic_Games_Account_Switcher_Globals
{
    public class AppSettings
    {
        private static AppSettings _instance;
        private static readonly object _lock = new object();
        private static readonly string SettingsPath = Path.Combine(Globals.UserDataFolder, "appsettings.json");

        public static AppSettings Instance
        {
            get
            {
                lock (_lock)
                {
                    if (_instance == null)
                    {
                        LoadSettings();
                    }
                    return _instance;
                }
            }
        }

        public List<string> RegistryKeysToSwap { get; set; } = new List<string>
        {
            @"HKCU\Software\Epic Games\Unreal Engine\Identifiers:AccountId",
            @"HKCU\Software\Epic Games\Unreal Engine\Identifiers:AppId",
            @"HKCU\Software\Epic Games\Unreal Engine\Identifiers:DeviceId",
            @"HKCU\Software\Epic Games\Unreal Engine\Identifiers:EpicAccountId",
            @"HKCU\Software\Epic Games\Unreal Engine\Identifiers:MachineId",
            @"HKCU\Software\Epic Games\Unreal Engine\EOS:WorkerId"
        };

        public bool MinimizeOnSwitch { get; set; } = true;
        public bool PromptOnUnsavedLogin { get; set; } = true;

        public static int WindowWidth { get; set; } = 1000;
        public static int WindowHeight { get; set; } = 650;
        public static int WindowLeft { get; set; } = -1;
        public static int WindowTop { get; set; } = -1;

        public static void LoadSettings()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(SettingsPath))
                    {
                        var json = File.ReadAllText(SettingsPath);
                        _instance = JsonConvert.DeserializeObject<AppSettings>(json);
                    }
                }
                catch (Exception ex)
                {
                    Globals.WriteToLog("Failed to load settings", ex);
                }

                if (_instance == null) _instance = new AppSettings();
            }
        }

        public static void SaveSettings()
        {
            Task.Run(() =>
            {
                lock (_lock)
                {
                    try
                    {
                        var json = JsonConvert.SerializeObject(Instance, Formatting.Indented);
                        File.WriteAllText(SettingsPath, json);
                    }
                    catch (Exception ex)
                    {
                        Globals.WriteToLog("Failed to save settings", ex);
                    }
                }
            });
        }
    }
}
