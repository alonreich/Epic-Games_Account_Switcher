using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Epic_Games_Account_Switcher_Globals
{
    public class Account : ObservableObject
    {
        public string Id { get; set; }
        
        private string _name;
        public string Name 
        { 
            get => _name; 
            set { _name = value; OnPropertyChanged(); } 
        }

        private string _email;
        public string Email
        {
            get => _email;
            set { _email = value; OnPropertyChanged(); }
        }

        private string _description;
        public string Description
        {
            get => _description;
            set { _description = value; OnPropertyChanged(); }
        }

        private string _profileImagePath;
        public string ProfileImagePath
        {
            get => _profileImagePath;
            set { _profileImagePath = value; OnPropertyChanged(); }
        }

        private string _accentColor = "#7AA2F7";
        public string AccentColor
        {
            get => _accentColor;
            set { _accentColor = value; OnPropertyChanged(); }
        }

        private bool _isActive;
        public bool IsActive 
        { 
            get => _isActive; 
            set { _isActive = value; OnPropertyChanged(); OnPropertyChanged(nameof(RecencyLabel)); } 
        }

        public string RememberMeData { get; set; }
        public Dictionary<string, string> RememberMeDataByPath { get; set; } = new Dictionary<string, string>();
        public string LauncherWindowSettings { get; set; }
        public Dictionary<string, string> LauncherWindowSettingsByPath { get; set; } = new Dictionary<string, string>();
        public List<CredentialService.SavedCredential> Credentials { get; set; } = new List<CredentialService.SavedCredential>();
        public Dictionary<string, string> RegistryData { get; set; } = new Dictionary<string, string>();

        public string EncryptedPassword { get; set; }

        private DateTime _lastPlayed;
        public DateTime LastPlayed
        {
            get => _lastPlayed;
            set
            {
                _lastPlayed = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasUsageHistory));
                OnPropertyChanged(nameof(LastPlayedDisplay));
                OnPropertyChanged(nameof(RecencyLabel));
                OnPropertyChanged(nameof(IsRecentlyUsed));
            }
        }

        private int _usageCount;
        public int UsageCount
        {
            get => _usageCount;
            set
            {
                _usageCount = Math.Max(0, value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(UsageLabel));
                OnPropertyChanged(nameof(UsageHeatWidth));
            }
        }

        [Newtonsoft.Json.JsonIgnore]
        public bool HasUsageHistory => LastPlayed > DateTime.MinValue.AddDays(1);

        [Newtonsoft.Json.JsonIgnore]
        public bool IsRecentlyUsed => HasUsageHistory && LastPlayed >= DateTime.Now.AddDays(-3);

        [Newtonsoft.Json.JsonIgnore]
        public string LastPlayedDisplay
        {
            get
            {
                if (!HasUsageHistory) return "Never used";
                var now = DateTime.Now;
                var span = now - LastPlayed;
                if (span.TotalMinutes < 1) return "Just now";
                if (span.TotalHours < 1) return ((int)span.TotalMinutes).ToString() + " min ago";
                if (LastPlayed.Date == now.Date) return "Today at " + LastPlayed.ToString("HH:mm");
                if (LastPlayed.Date == now.Date.AddDays(-1)) return "Yesterday at " + LastPlayed.ToString("HH:mm");
                if (span.TotalDays < 7) return ((int)span.TotalDays).ToString() + " days ago";
                return LastPlayed.ToString("dd/MM/yyyy");
            }
        }

        [Newtonsoft.Json.JsonIgnore]
        public string RecencyLabel
        {
            get
            {
                if (IsActive) return "ACTIVE NOW";
                if (!HasUsageHistory) return "READY";
                if (IsRecentlyUsed) return "RECENT";
                return "RESTING";
            }
        }

        [Newtonsoft.Json.JsonIgnore]
        public string UsageLabel
        {
            get
            {
                if (UsageCount <= 0) return "No tracked switches";
                if (UsageCount == 1) return "1 tracked switch";
                return UsageCount.ToString() + " tracked switches";
            }
        }

        [Newtonsoft.Json.JsonIgnore]
        public double UsageHeatWidth => UsageCount <= 0 ? 16 : Math.Min(112, 20 + (UsageCount * 12));

        [Newtonsoft.Json.JsonIgnore]
        public string Password
        {
            get
            {
                if (string.IsNullOrEmpty(EncryptedPassword)) return string.Empty;
                byte[] decryptedBytes = null;
                try
                {
                    byte[] encryptedBytes = Convert.FromBase64String(EncryptedPassword);
                    decryptedBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(decryptedBytes);
                }
                catch
                {
                    return string.Empty;
                }
                finally
                {
                    if (decryptedBytes != null) Array.Clear(decryptedBytes, 0, decryptedBytes.Length);
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
                    byte[] plainBytes = null;
                    try
                    {
                        plainBytes = Encoding.UTF8.GetBytes(value);
                        byte[] encryptedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
                        EncryptedPassword = Convert.ToBase64String(encryptedBytes);
                    }
                    catch (Exception ex)
                    {
                        Globals.WriteToLog("[DPAPI] Cryptographic exception encrypting password string: " + ex.Message, ex);
                        EncryptedPassword = null;
                    }
                    finally
                    {
                        if (plainBytes != null) Array.Clear(plainBytes, 0, plainBytes.Length);
                    }
                }
                OnPropertyChanged();
            }
        }

        public SecureString GetSecurePassword()
        {
            var sec = new SecureString();
            if (string.IsNullOrEmpty(EncryptedPassword)) return sec;
            byte[] decryptedBytes = null;
            try
            {
                byte[] encryptedBytes = Convert.FromBase64String(EncryptedPassword);
                decryptedBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
                char[] chars = Encoding.UTF8.GetChars(decryptedBytes);
                try
                {
                    foreach (char c in chars) sec.AppendChar(c);
                    sec.MakeReadOnly();
                }
                finally
                {
                    Array.Clear(chars, 0, chars.Length);
                }
                return sec;
            }
            catch
            {
                return new SecureString();
            }
            finally
            {
                if (decryptedBytes != null) Array.Clear(decryptedBytes, 0, decryptedBytes.Length);
            }
        }

        public void SetSecurePassword(SecureString securePassword)
        {
            if (securePassword == null || securePassword.Length == 0)
            {
                EncryptedPassword = null;
                OnPropertyChanged(nameof(Password));
                return;
            }
            IntPtr ptr = IntPtr.Zero;
            byte[] plainBytes = null;
            try
            {
                ptr = Marshal.SecureStringToGlobalAllocUnicode(securePassword);
                string tempStr = Marshal.PtrToStringUni(ptr);
                plainBytes = Encoding.UTF8.GetBytes(tempStr);
                byte[] encryptedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
                EncryptedPassword = Convert.ToBase64String(encryptedBytes);
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("[DPAPI] Cryptographic exception encrypting secure password: " + ex.Message, ex);
                EncryptedPassword = null;
            }
            finally
            {
                if (ptr != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(ptr);
                if (plainBytes != null) Array.Clear(plainBytes, 0, plainBytes.Length);
            }
            OnPropertyChanged(nameof(Password));
        }

        public Account(string id, string name)
        {
            Id = id;
            _name = name;
        }
    }
}
