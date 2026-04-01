using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Epic_Games_Account_Switcher_Client
{
    public partial class CaptureAccountWindow : Window
    {
        public string AccountName { get; private set; }
        public string AccountDescription { get; private set; }
        public string ProfileImagePath { get; private set; }

        public string AccountEmail { get; private set; }

        public CaptureAccountWindow(string name = "", string description = "", string email = "", string imagePath = "")
        {
            InitializeComponent();
            
            AccountName = name;
            AccountDescription = description;
            AccountEmail = email;
            ProfileImagePath = imagePath;

            AccountNameInput.Text = name;
            AccountDescriptionInput.Text = description;
            AccountEmailInput.Text = string.IsNullOrEmpty(email) ? "Unknown Email" : email;

            UpdateProfileImage();
            AccountNameInput.Focus();
        }

        private void UpdateProfileImage()
        {
            try
            {
                if (string.IsNullOrEmpty(ProfileImagePath)) return;
                ProfileImageBrush.ImageSource = new BitmapImage(new Uri(ProfileImagePath, UriKind.RelativeOrAbsolute));
            }
            catch { }
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
            if (!string.IsNullOrWhiteSpace(AccountNameInput.Text))
            {
                AccountName = AccountNameInput.Text;
                AccountDescription = AccountDescriptionInput.Text;
                AccountEmail = AccountEmailInput.Text;
                DialogResult = true;
                Close();
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
