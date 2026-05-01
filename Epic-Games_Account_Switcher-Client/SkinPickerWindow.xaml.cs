using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Epic_Games_Account_Switcher_Globals;

namespace Epic_Games_Account_Switcher_Client
{
    public partial class SkinPickerWindow : Window
    {
        public string SelectedSkinPath { get; set; }
        public class SkinItem { public string Name { get; set; } public string FullPath { get; set; } }
        public SkinPickerWindow() { InitializeComponent(); LoadSkins(); }
        private void LoadSkins()
        {
            var skins = new List<SkinItem>();
            var paths = new[] { Path.Combine(Globals.UserDataFolder, "Skin_Icons"), Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Skin_Icons"), Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Payload", "Skin_Icons") };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in paths)
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories))
                {
                    var ext = Path.GetExtension(f).ToLower(); if (ext != ".jpg" && ext != ".png" && ext != ".jpeg") continue;
                    var fn = Path.GetFileName(f); if (seen.Contains(fn)) continue;
                    seen.Add(fn); skins.Add(new SkinItem { Name = Path.GetFileNameWithoutExtension(f).Replace("_", " "), FullPath = f });
                }
            }
            SkinsList.ItemsSource = skins;
        }
        private void SkinsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) { Select_Click(null, null); }
        private void Select_Click(object sender, RoutedEventArgs e)
        {
            var skin = SkinsList.SelectedItem as SkinItem;
            if (skin != null) { SelectedSkinPath = skin.FullPath; DialogResult = true; Close(); }
        }
        private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
    }
}
