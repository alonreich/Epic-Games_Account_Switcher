using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Epic_Games_Account_Switcher_Globals;

namespace Epic_Games_Account_Switcher_Client
{
    public partial class SkinPickerWindow : Window
    {
        private bool _isClosing;
        public string SelectedSkinPath { get; set; }
        public class SkinItem
        {
            public string Name { get; set; }
            public string FullPath { get; set; }
            public bool IsCustom { get; set; }
            /// <summary>Loaded into memory (small decode) so the file is never locked and can be removed.</summary>
            public BitmapImage Image { get; set; }
            public string AccessibleName => IsCustom ? L.T("Your photo {0}", Name) : L.T("Avatar {0}", Name);
            public string ToolTipText => IsCustom ? L.T("{0} (your photo — right-click or press Delete to remove it)", Name) : Name;
            public override string ToString() => Name ?? "";
        }
        public SkinPickerWindow(string currentPath = null)
        {
            InitializeComponent();
            DialogWindowHelper.FitToScreen(this);
            LoadSkins();
            PreselectCurrent(currentPath);
            Loaded += (s, e) => FocusSelectedTile();
        }

        private void PreselectCurrent(string currentPath)
        {
            if (string.IsNullOrWhiteSpace(currentPath)) return;
            var fileName = Path.GetFileName(currentPath);
            var match = (SkinsList.ItemsSource as IEnumerable<SkinItem>)?.FirstOrDefault(s =>
                string.Equals(s.FullPath, currentPath, StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFileName(s.FullPath), fileName, StringComparison.OrdinalIgnoreCase));
            if (match == null) return;
            SkinsList.SelectedItem = match;
            SkinsList.ScrollIntoView(match);
        }

        private void FocusSelectedTile()
        {
            var item = SkinsList.SelectedItem ?? SkinsList.Items.Cast<object>().FirstOrDefault();
            if (item == null) { SkinsList.Focus(); return; }
            SkinsList.UpdateLayout();
            var container = SkinsList.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem;
            var tile = container == null ? null : FindChild<Button>(container);
            if (tile != null) tile.Focus(); else SkinsList.Focus();
        }

        private static T FindChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T typed) return typed;
                var nested = FindChild<T>(child);
                if (nested != null) return nested;
            }
            return null;
        }

        private void SkinsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SelectButton.IsEnabled = SkinsList.SelectedItem != null;
        }
        private void LoadSkins()
        {
            var skins = new List<SkinItem>();
            foreach (var skin in SkinLibrary.GetAll())
            {
                var image = LoadThumbnail(skin.FullPath);
                if (image == null) continue;
                skins.Add(new SkinItem { Name = skin.Name, FullPath = skin.FullPath, IsCustom = skin.IsCustom, Image = image });
            }
            SkinsList.ItemsSource = skins;
        }

        private static BitmapImage LoadThumbnail(string path)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 240;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("[SKINS] Skipped unreadable avatar " + path, ex);
                return null;
            }
        }

        private void Upload_Click(object sender, RoutedEventArgs e)
        {
            var saved = ImageCropWindow.PickCropAndSave(this);
            if (saved == null) return;
            LoadSkins();
            PreselectCurrent(saved);
            FocusSelectedTile();
        }

        private void SkinTile_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            // Only uploaded photos have a menu (Remove); shipped avatars cannot be removed.
            var skin = (sender as FrameworkElement)?.DataContext as SkinItem;
            if (skin == null || !skin.IsCustom) e.Handled = true;
        }

        private void RemovePhoto_Click(object sender, RoutedEventArgs e)
        {
            var menu = (sender as MenuItem)?.Parent as ContextMenu;
            var skin = (menu?.PlacementTarget as FrameworkElement)?.DataContext as SkinItem;
            RemovePhoto(skin);
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Delete) return;
            var skin = SkinsList.SelectedItem as SkinItem;
            if (skin == null || !skin.IsCustom) return;
            RemovePhoto(skin);
            e.Handled = true;
        }

        private void RemovePhoto(SkinItem skin)
        {
            if (skin == null || !skin.IsCustom) return;
            var users = AccountService.GetAccounts()
                .Where(a => string.Equals(a.ProfileImagePath, skin.FullPath, StringComparison.OrdinalIgnoreCase))
                .Select(a => a.Name).ToList();
            if (users.Count > 0)
            {
                new CustomMessageBox(L.T("Photo In Use"), L.T("This photo is used by: {0}. Choose another avatar for those profiles first, then remove it.", string.Join(", ", users))) { Owner = this }.ShowDialog();
                return;
            }
            var confirm = new CustomMessageBox(L.T("Remove Photo"), L.T("Remove the photo \"{0}\" from your avatars?", skin.Name), L.T("REMOVE"), null, L.T("KEEP"), MessageBoxVisualStyle.Danger) { Owner = this };
            confirm.ShowDialog();
            if (confirm.Result != CustomDialogResult.Primary) return;
            try
            {
                SkinLibrary.DeleteCustom(skin.FullPath);
                if (string.Equals(SelectedSkinPath, skin.FullPath, StringComparison.OrdinalIgnoreCase)) SelectedSkinPath = null;
                LoadSkins();
                FocusSelectedTile();
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("[SKINS] Could not remove photo " + skin.FullPath, ex);
                new CustomMessageBox(L.T("Photo Not Removed"), L.T("The photo could not be removed: {0}", ex.Message), MessageBoxVisualStyle.Error) { Owner = this }.ShowDialog();
            }
        }
        private void Dialog_DragMove(object sender, MouseButtonEventArgs e) => DialogWindowHelper.DragMove(this, e);
        private void SkinsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) { SelectCurrentSkin(); }
        private void SkinTile_Click(object sender, RoutedEventArgs e)
        {
            SelectSkinFromSender(sender);
        }
        private void SkinTile_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            SelectSkinFromSender(sender);
            SelectCurrentSkin();
            e.Handled = true;
        }
        private void SelectSkinFromSender(object sender)
        {
            var element = sender as FrameworkElement;
            var skin = element == null ? null : element.DataContext as SkinItem;
            if (skin == null) return;
            SkinsList.SelectedItem = skin;
            SelectedSkinPath = skin.FullPath;
        }
        private void Select_Click(object sender, RoutedEventArgs e)
        {
            SelectCurrentSkin();
        }
        private bool SelectCurrentSkin()
        {
            if (_isClosing) return false;
            var skin = SkinsList.SelectedItem as SkinItem;
            if (skin == null) return false;
            SelectedSkinPath = skin.FullPath;
            _isClosing = true;
            DialogResult = true;
            Close();
            return true;
        }
        private void Cancel_Click(object sender, RoutedEventArgs e) 
        { 
            if (_isClosing) return;
            _isClosing = true;
            DialogResult = false; 
            Close(); 
        }
    }
}
