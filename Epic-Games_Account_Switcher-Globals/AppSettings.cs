using System;
using System.Collections.Generic;
using System.IO;
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

        public bool PromptOnUnsavedLogin { get; set; } = true;
        public bool CloseOnceLaunched { get; set; } = false;
        public bool OpenEpicLauncherOnStartup { get; set; } = false;
        public int ModernAuthBlobMaxAgeDays { get; set; } = 30;
        public bool MinimizeToTray { get; set; } = false;

        public List<string> DismissedUnsavedLoginIds { get; set; } = new List<string>();

        public int WindowWidth { get; set; } = 1250;
        public int WindowHeight { get; set; } = 880;
        public int WindowLeft { get; set; } = -1;
        public int WindowTop { get; set; } = -1;
        public int WindowRelativeLeft { get; set; } = -1;
        public int WindowRelativeTop { get; set; } = -1;
        public string WindowMonitorDeviceName { get; set; } = "";
        public double ZoomFactor { get; set; } = 1.0;

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
                if (_instance.DismissedUnsavedLoginIds == null) _instance.DismissedUnsavedLoginIds = new List<string>();
                if (_instance.ZoomFactor < 0.35 || _instance.ZoomFactor > 2.50) _instance.ZoomFactor = 1.0;
            }
        }

        public static void SaveSettings()
        {
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                    if (_instance == null) _instance = new AppSettings();
                    var json = JsonConvert.SerializeObject(_instance, Formatting.Indented);
                    var tmpPath = SettingsPath + ".tmp_" + Guid.NewGuid().ToString("N");
                    File.WriteAllText(tmpPath, json);
                    if (File.Exists(SettingsPath))
                    {
                        var bakPath = SettingsPath + ".bak_" + Guid.NewGuid().ToString("N");
                        File.Replace(tmpPath, SettingsPath, bakPath, true);
                        try { if (File.Exists(bakPath)) File.Delete(bakPath); } catch { }
                    }
                    else
                    {
                        File.Move(tmpPath, SettingsPath);
                    }
                }
                catch (Exception ex)
                {
                    Globals.WriteToLog("Failed to save settings", ex);
                }
            }
        }
    }
}
