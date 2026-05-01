using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Epic_Games_Account_Switcher_Globals
{
    public class GLang
    {
        private static GLang _instance;
        private static readonly object LockObj = new object();

        public static GLang Instance
        {
            get
            {
                lock (LockObj)
                {
                    if (_instance != null && _instance._currentlyModifying) return _instance;
                    if (_instance == null)
                    {
                        _instance = new GLang { _currentlyModifying = true };
                        LoadLocalised();
                        _instance._currentlyModifying = false;
                    }
                    return _instance;
                }
            }
        }

        private Dictionary<string, string> _strings = new Dictionary<string, string>();
        private string _current = "";
        private bool _currentlyModifying;

        public static Dictionary<string, string> Strings { get => Instance._strings; set => Instance._strings = value; }
        public static string Current { get => Instance._current; set => Instance._current = value; }

        public string this[string key] => Strings.ContainsKey(key) ? Strings[key] : key;

        public static void LoadDefault()
        {
            Load("en-US");
        }

        public static void LoadLocalised()
        {
            LoadDefault();
            Load(GLangAppSettings.Language == "" ? CultureInfo.CurrentCulture.Name : GLangAppSettings.Language);
        }

        public static List<string> GetAvailableLanguages() 
        {
            string resourcesPath = Path.Combine(Globals.AppDataFolder, "Resources");
            if (!Directory.Exists(resourcesPath)) return new List<string>();
            return Directory.GetFiles(resourcesPath).Select(f => Path.GetFileName(f).Replace(".yml", "")).ToList();
        }

        public static bool Load(string filename, bool save = false)
        {
            try
            {
                var path = Path.Combine(Globals.AppDataFolder, "Resources", filename + ".yml");
                Current = filename;

                if (!File.Exists(path))
                {
                    var availableLang = GetAvailableLanguages();
                    var langGroup = filename.Split('-')[0];
                    var foundClose = false;
                    foreach (var l in availableLang.Where(l => l.StartsWith(langGroup)))
                    {
                        path = Path.Combine(Globals.AppDataFolder, "Resources", l + ".yml");
                        foundClose = true;
                        Current = l;
                        if (save) GLangAppSettings.Language = l;
                        break;
                    }
                    if (!foundClose) return false;
                }

                var desc = new DeserializerBuilder().WithNamingConvention(HyphenatedNamingConvention.Instance).Build();
                var text = Globals.ReadAllText(path);
                var dict = JsonConvert.DeserializeObject<Dictionary<string, string>>(JsonConvert.SerializeObject(desc.Deserialize<object>(text)));
                if (dict != null) foreach (var kvp in dict) Strings[kvp.Key] = kvp.Value;
                return true;
            }
            catch (Exception e)
            {
                Globals.WriteToLog("Globals-Lang error", e);
                return false;
            }
        }
    }

    class GLangAppSettings
    {
        private static GLangAppSettings _instance = new GLangAppSettings();
        private static readonly object LockObj = new object();
        public static GLangAppSettings Instance
        {
            get
            {
                lock (LockObj)
                {
                    if (_instance != null && _instance._currentlyModifying) return _instance;
                    _instance = new GLangAppSettings { _currentlyModifying = true };
                    if (File.Exists(SettingsFile))
                    {
                        _instance = JsonConvert.DeserializeObject<GLangAppSettings>(File.ReadAllText(SettingsFile));
                        if (_instance == null) _instance = new GLangAppSettings();
                    }
                    _instance._currentlyModifying = false;
                    return _instance;
                }
            }
        }

        private bool _currentlyModifying;
        public static readonly string SettingsFile = "WindowSettings.json";
        [JsonProperty("Language")] private string _lang = "";
        public static string Language { get => Instance._lang; set => Instance._lang = value; }
    }
}
