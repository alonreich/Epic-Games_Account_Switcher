using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Epic_Games_Account_Switcher_Globals;

namespace Epic_Games_Account_Switcher_Client
{
    public partial class CaptureAccountWindow : Window
    {
        public string AccountName { get; private set; }
        public string AccountDescription { get; private set; }
        public string ProfileImagePath { get; private set; }

        public string AccountEmail { get; private set; }
        public string AccountPassword { get; private set; }

        public CaptureAccountWindow(string name = "", string description = "", string email = "", string imagePath = "", string password = "")
        {
            InitializeComponent();
            
            AccountName = name;
            AccountDescription = description;
            AccountEmail = email;
            AccountPassword = password;
            ProfileImagePath = string.IsNullOrWhiteSpace(imagePath) ? AccountService.GetDefaultProfileImagePath() : imagePath;

            AccountNameInput.Text = string.IsNullOrEmpty(name) ? "" : name;
            AccountDescriptionInput.Text = description;
            AccountEmailInput.Text = string.IsNullOrWhiteSpace(email) || email == "N/A" ? "" : email;
            AccountPasswordInput.Password = string.IsNullOrWhiteSpace(password) ? "" : password;

            UpdateProfileImage();
            AccountNameInput.Focus();
        }

        private void UpdateProfileImage()
        {
            var candidates = new[]
            {
                ProfileImagePath,
                AccountService.GetDefaultProfileImagePath(),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Skin_Icons", "Lizard_Skin.jpg"),
                Path.GetFullPath("Payload\\Skin_Icons\\Lizard_Skin.jpg"),
                Path.GetFullPath("Skin_Icons\\Lizard_Skin.jpg")
            };

            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                    continue;

                var resolved = candidate;

                try
                {
                    if (!resolved.StartsWith("pack://", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!Path.IsPathRooted(resolved))
                            resolved = Path.GetFullPath(resolved);

                        if (!File.Exists(resolved))
                            continue;
                    }

                    ProfileImageBrush.ImageSource = new BitmapImage(new Uri(resolved, UriKind.RelativeOrAbsolute));
                    ProfileImagePath = resolved;
                    return;
                }
                catch
                {
                }
            }

            ProfileImagePath = AccountService.GetDefaultProfileImagePath();
        }

        private void SelectImage_Click(object sender, RoutedEventArgs e)
        {
            var picker = new SkinPickerWindow { Owner = this };
            if (picker.ShowDialog() == true)
            {
                ProfileImagePath = picker.SelectedSkinPath;
                UpdateProfileImage();
            }
        }

        private void Capture_Click(object sender, RoutedEventArgs e)
        {
            var friendlyName = AccountNameInput.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(friendlyName))
            {
                MessageBox.Show("Friendly Name is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                AccountNameInput.Focus();
                return;
            }

            AccountName = friendlyName;
            AccountDescription = AccountDescriptionInput.Text ?? string.Empty;
            AccountEmail = AccountEmailInput.Text?.Trim() ?? string.Empty;
            AccountPassword = AccountPasswordInput.Password ?? string.Empty;
            ProfileImagePath = string.IsNullOrWhiteSpace(ProfileImagePath) ? AccountService.GetDefaultProfileImagePath() : ProfileImagePath;

            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
