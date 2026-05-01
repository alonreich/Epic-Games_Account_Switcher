using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace Epic_Games_Account_Switcher_Globals
{
    public class Account : INotifyPropertyChanged
    {
        public string Id { get; set; }
        
        private string _name;
        public string Name 
        { 
            get => _name; 
            set { _name = value; OnPropertyChanged(); } 
        }

        public string Email { get; set; }
        public string Description { get; set; }
        public string ProfileImagePath { get; set; }
        public string AccentColor { get; set; } = "#7AA2F7";

        private bool _isActive;
        public bool IsActive 
        { 
            get => _isActive; 
            set { _isActive = value; OnPropertyChanged(); } 
        }

        public string RememberMeData { get; set; }
        public Dictionary<string, string> RememberMeDataByPath { get; set; } = new Dictionary<string, string>();
        public string LauncherWindowSettings { get; set; }
        public Dictionary<string, string> LauncherWindowSettingsByPath { get; set; } = new Dictionary<string, string>();
        public List<CredentialService.SavedCredential> Credentials { get; set; } = new List<CredentialService.SavedCredential>();
        public Dictionary<string, string> RegistryData { get; set; } = new Dictionary<string, string>();

        public string EncryptedPassword { get; set; }

        public DateTime LastPlayed { get; set; }

        [Newtonsoft.Json.JsonIgnore]
        public string Password
        {
            get
            {
                if (string.IsNullOrEmpty(EncryptedPassword)) return string.Empty;
                try
                {
                    byte[] encryptedBytes = Convert.FromBase64String(EncryptedPassword);
                    byte[] decryptedBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(decryptedBytes);
                }
                catch
                {
                    return string.Empty;
                }
            }
            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    EncryptedPassword = null;
                }
                else
                {
                    byte[] plainBytes = Encoding.UTF8.GetBytes(value);
                    byte[] encryptedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
                    EncryptedPassword = Convert.ToBase64String(encryptedBytes);
                }
                OnPropertyChanged();
            }
        }

        public Account(string id, string name)
        {
            Id = id;
            _name = name;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
