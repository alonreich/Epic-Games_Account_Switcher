using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using Epic_Games_Account_Switcher_Globals;

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
                var skinMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;

                // Paths to scan
                var searchPaths = new List<string>
                {
                    Path.Combine(Globals.UserDataFolder, "Skin_Icons"),
                    Path.Combine(baseDir, "Skin_Icons"),
                    Path.Combine(baseDir, "..", "Payload", "Skin_Icons"),
                    @"C:\Full_Control\Epic-Games_Account_Switcher\Skin_Icons"
                };

                foreach (var path in searchPaths)
                {
                    if (!Directory.Exists(path)) continue;

                    var files = Directory.GetFiles(path, "*.*")
                        .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                    f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                    f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                                    f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase));

                    foreach (var file in files)
                    {
                        var fileName = Path.GetFileName(file);
                        // Favor earlier paths (UserData) if duplicate filename found
                        if (!skinMap.ContainsKey(fileName))
                        {
                            skinMap[fileName] = file;
                        }
                    }
                }

                Globals.WriteToLog($"[UI] Total unique skins identified: {skinMap.Count}");

                var finalItems = skinMap.Values
                    .Select(f => new SkinItem { FullPath = f })
                    .OrderBy(s => Path.GetFileName(s.FullPath))
                    .ToList();

                if (finalItems.Count == 0)
                {
                    Globals.WriteToLog("[ERROR] No skin icons found in any search path!");
                    return;
                }

                SkinsList.ItemsSource = finalItems;

                var defaultGuy = finalItems.FirstOrDefault(f => f.FullPath.Contains("Default_Skin_Guy"));
                if (defaultGuy != null)
                {
                    SkinsList.SelectedItem = defaultGuy;
                }
                else if (finalItems.Count > 0)
                {
                    SkinsList.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("[ERROR] Critical error loading skin picker.", ex);
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
