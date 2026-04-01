using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace Epic_Games_Account_Switcher_Client
{
    public partial class SkinPickerWindow : Window
    {
        public string SelectedSkinPath { get; private set; }

        public SkinPickerWindow()
        {
            InitializeComponent();
            LoadSkins();
        }

        private void LoadSkins()
        {
            try
            {
                // Look in the application's base directory
                string skinsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Skins_Icons");
                if (!Directory.Exists(skinsDir))
                {
                    // Fallback to current directory for development
                    skinsDir = Path.GetFullPath("Skins_Icons");
                }

                if (!Directory.Exists(skinsDir)) return;

                var files = Directory.GetFiles(skinsDir, "*.*")
                    .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || 
                                f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || 
                                f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) || 
                                f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
                    .Select(f => new SkinItem { FullPath = f })
                    .ToList();

                SkinsList.ItemsSource = files;

                // Pre-select Default_Skin_Guy if it exists, otherwise first item
                var defaultGuy = files.FirstOrDefault(f => f.FullPath.Contains("Default_Skin_Guy"));
                if (defaultGuy != null)
                {
                    SkinsList.SelectedItem = defaultGuy;
                }
                else if (files.Count > 0)
                {
                    SkinsList.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading skins: " + ex.Message);
            }
        }

        private void Select_Click(object sender, RoutedEventArgs e)
        {
            ConfirmSelection();
        }

        private void SkinsList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            ConfirmSelection();
        }

        private void ConfirmSelection()
        {
            if (SkinsList.SelectedItem is SkinItem item)
            {
                SelectedSkinPath = item.FullPath;
                DialogResult = true;
                Close();
            }
            else
            {
                // Fallback to first item if somehow nothing is selected
                if (SkinsList.Items.Count > 0)
                {
                    SelectedSkinPath = ((SkinItem)SkinsList.Items[0]).FullPath;
                    DialogResult = true;
                    Close();
                }
                else
                {
                    MessageBox.Show("No skins available to select.");
                }
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }

    public class SkinItem
    {
        public string FullPath { get; set; }
    }
}
