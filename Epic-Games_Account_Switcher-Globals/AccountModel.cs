using System;
using System.Collections.Generic;

namespace Epic_Games_Account_Switcher_Globals
{
    public class Account
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Email { get; set; }
        public string Description { get; set; }
        public string ProfileImagePath { get; set; }
        public string AccentColor { get; set; } = "#7AA2F7";
        public bool IsActive { get; set; }
        public string SourceWebCacheName { get; set; } = "webcache";
        public Dictionary<string, string> RegistryData { get; set; } = new();

        public Account(string id, string name)
        {
            Id = id;
            Name = name;
        }
    }
}
