using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Epic_Games_Account_Switcher_Globals;

namespace Epic_Games_Account_Switcher_Client
{
    /// <summary>View-model side of the experimental launcher frame (see LauncherFrameController).</summary>
    public partial class MainViewModel
    {
        public bool ExperimentalLauncherFrameSetting
        {
            get => AppSettings.Instance.ExperimentalLauncherFrame;
            set
            {
                if (AppSettings.Instance.ExperimentalLauncherFrame == value) return;
                AppSettings.Instance.ExperimentalLauncherFrame = value;
                AppSettings.SaveSettings();
                OnPropertyChanged();
                if (Application.Current?.MainWindow is MainWindow win) win.ApplyLauncherFrameSetting(value);
            }
        }

        public List<Account> FrameLeftAccounts => Accounts == null ? new List<Account>() : Accounts.Where((a, i) => i % 2 == 0).ToList();
        public List<Account> FrameRightAccounts => Accounts == null ? new List<Account>() : Accounts.Where((a, i) => i % 2 == 1).ToList();

        public void RefreshFrameAccounts()
        {
            OnPropertyChanged(nameof(FrameLeftAccounts));
            OnPropertyChanged(nameof(FrameRightAccounts));
        }

        private ICommand _disableLauncherFrameCommand;
        public ICommand DisableLauncherFrameCommand => _disableLauncherFrameCommand ?? (_disableLauncherFrameCommand = new RelayCommand(() => ExperimentalLauncherFrameSetting = false));
    }
}
