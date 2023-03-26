﻿using System.Collections.ObjectModel;
using NETworkManager.Controls;
using Dragablz;
using MahApps.Metro.Controls.Dialogs;
using System.Windows.Input;
using NETworkManager.Views;
using NETworkManager.Settings;
using System.ComponentModel;
using System.Windows.Data;
using System;
using System.Linq;
using System.Diagnostics;
using NETworkManager.Utilities;
using System.Windows;
using NETworkManager.Models.RemoteDesktop;
using NETworkManager.Profiles;
using System.Windows.Threading;
using NETworkManager.Models;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace NETworkManager.ViewModels;

public class RemoteDesktopHostViewModel : ViewModelBase, IProfileManager
{
    #region Variables
    private readonly IDialogCoordinator _dialogCoordinator;
    private readonly DispatcherTimer _searchDispatcherTimer = new();

    public IInterTabClient InterTabClient { get; }
    public ObservableCollection<DragablzTabItem> TabItems { get; }

    private readonly bool _isLoading = true;
    private bool _isViewActive = true;

    private int _selectedTabIndex;
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (value == _selectedTabIndex)
                return;

            _selectedTabIndex = value;
            OnPropertyChanged();
        }
    }

    #region Profiles
    public ICollectionView _profiles;
    public ICollectionView Profiles
    {
        get => _profiles;
        set
        {
            if (value == _profiles)
                return;

            _profiles = value;
            OnPropertyChanged();
        }
    }

    private ProfileInfo _selectedProfile = new();
    public ProfileInfo SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (value == _selectedProfile)
                return;

            _selectedProfile = value;
            OnPropertyChanged();
        }
    }

    private string _search;
    public string Search
    {
        get => _search;
        set
        {
            if (value == _search)
                return;

            _search = value;

            // Start searching...
            IsSearching = true;
            _searchDispatcherTimer.Start();

            OnPropertyChanged();
        }
    }

    private bool _isSearching;
    public bool IsSearching
    {
        get => _isSearching;
        set
        {
            if (value == _isSearching)
                return;

            _isSearching = value;
            OnPropertyChanged();
        }
    }

    private bool _canProfileWidthChange = true;
    private double _tempProfileWidth;

    private bool _expandProfileView;
    public bool ExpandProfileView
    {
        get => _expandProfileView;
        set
        {
            if (value == _expandProfileView)
                return;

            if (!_isLoading)
                SettingsManager.Current.RemoteDesktop_ExpandProfileView = value;

            _expandProfileView = value;

            if (_canProfileWidthChange)
                ResizeProfile(false);

            OnPropertyChanged();
        }
    }

    private GridLength _profileWidth;
    public GridLength ProfileWidth
    {
        get => _profileWidth;
        set
        {
            if (value == _profileWidth)
                return;

            if (!_isLoading && Math.Abs(value.Value - GlobalStaticConfiguration.Profile_WidthCollapsed) > GlobalStaticConfiguration.Profile_FloatPointFix) // Do not save the size when collapsed
                SettingsManager.Current.RemoteDesktop_ProfileWidth = value.Value;

            _profileWidth = value;

            if (_canProfileWidthChange)
                ResizeProfile(true);

            OnPropertyChanged();
        }
    }
    #endregion
    #endregion

    #region Constructor, load settings
    public RemoteDesktopHostViewModel(IDialogCoordinator instance)
    {
        _dialogCoordinator = instance;

        InterTabClient = new DragablzInterTabClient(ApplicationName.RemoteDesktop);

        TabItems = new ObservableCollection<DragablzTabItem>();

        // Profiles
        SetProfilesView();

        ProfileManager.OnProfilesUpdated += ProfileManager_OnProfilesUpdated;

        _searchDispatcherTimer.Interval = GlobalStaticConfiguration.SearchDispatcherTimerTimeSpan;
        _searchDispatcherTimer.Tick += SearchDispatcherTimer_Tick;

        LoadSettings();

        _isLoading = false;
    }

    private void LoadSettings()
    {
        ExpandProfileView = SettingsManager.Current.RemoteDesktop_ExpandProfileView;

        ProfileWidth = ExpandProfileView ? new GridLength(SettingsManager.Current.RemoteDesktop_ProfileWidth) : new GridLength(GlobalStaticConfiguration.Profile_WidthCollapsed);

        _tempProfileWidth = SettingsManager.Current.RemoteDesktop_ProfileWidth;
    }
    #endregion

    #region ICommand & Actions        
    public ICommand ConnectCommand => new RelayCommand(p => ConnectAction());

    private void ConnectAction()
    {
        Connect();
    }

    private bool IsConnected_CanExecute(object view)
    {
        if (view is RemoteDesktopControl control)
            return control.IsConnected;

        return false;
    }

    private bool IsDisconnected_CanExecute(object view)
    {
        if (view is RemoteDesktopControl control)
            return !control.IsConnected;

        return false;
    }

    public ICommand ReconnectCommand => new RelayCommand(ReconnectAction, IsDisconnected_CanExecute);

    private void ReconnectAction(object view)
    {
        if (view is RemoteDesktopControl control)
        {
            if (control.ReconnectCommand.CanExecute(null))
                control.ReconnectCommand.Execute(null);
        }
    }

    public ICommand DisconnectCommand => new RelayCommand(DisconnectAction, IsConnected_CanExecute);

    private void DisconnectAction(object view)
    {
        if (view is RemoteDesktopControl control)
        {
            if (control.DisconnectCommand.CanExecute(null))
                control.DisconnectCommand.Execute(null);
        }
    }

    public ICommand FullscreenCommand => new RelayCommand(FullscreenAction, IsConnected_CanExecute);

    private void FullscreenAction(object view)
    {
        if (view is RemoteDesktopControl control)
            control.FullScreen();
    }

    public ICommand AdjustScreenCommand => new RelayCommand(AdjustScreenAction, IsConnected_CanExecute);

    private void AdjustScreenAction(object view)
    {
        if (view is RemoteDesktopControl control)
            control.AdjustScreen();
    }

    public ICommand SendCtrlAltDelCommand => new RelayCommand(SendCtrlAltDelAction, IsConnected_CanExecute);

    private async void SendCtrlAltDelAction(object view)
    {
        if (view is RemoteDesktopControl control)
        {
            try
            {
                control.SendKey(Keystroke.CtrlAltDel);
            }
            catch (Exception ex)
            {
                ConfigurationManager.Current.IsDialogOpen = true;

                await _dialogCoordinator.ShowMessageAsync(this, Localization.Resources.Strings.Error, string.Format("{0}\n\nMessage:\n{1}", Localization.Resources.Strings.CouldNotSendKeystroke, ex.Message, MessageDialogStyle.Affirmative, AppearanceManager.MetroDialog));

                ConfigurationManager.Current.IsDialogOpen = false;
            }
        }
    }

    public ICommand ConnectProfileCommand => new RelayCommand(p => ConnectProfileAction(), ConnectProfile_CanExecute);

    private bool ConnectProfile_CanExecute(object obj)
    {
        return !IsSearching && SelectedProfile != null;
    }

    private void ConnectProfileAction()
    {
        ConnectProfile();
    }

    public ICommand ConnectProfileAsCommand => new RelayCommand(p => ConnectProfileAsAction());

    private void ConnectProfileAsAction()
    {
        ConnectProfileAs();
    }

    public ICommand ConnectProfileExternalCommand => new RelayCommand(p => ConnectProfileExternalAction());

    private void ConnectProfileExternalAction()
    {
        Process.Start("mstsc.exe", $"/V:{SelectedProfile.RemoteDesktop_Host}");
    }

    public ICommand AddProfileCommand => new RelayCommand(p => AddProfileAction());

    private void AddProfileAction()
    {
        ProfileDialogManager.ShowAddProfileDialog(this, _dialogCoordinator, null, null, ApplicationName.RemoteDesktop);
    }

    private bool ModifyProfile_CanExecute(object obj) => SelectedProfile != null && !SelectedProfile.IsDynamic;

    public ICommand EditProfileCommand => new RelayCommand(p => EditProfileAction(), ModifyProfile_CanExecute);

    private void EditProfileAction()
    {
        ProfileDialogManager.ShowEditProfileDialog(this, _dialogCoordinator, SelectedProfile);
    }

    public ICommand CopyAsProfileCommand => new RelayCommand(p => CopyAsProfileAction(), ModifyProfile_CanExecute);

    private void CopyAsProfileAction()
    {
        ProfileDialogManager.ShowCopyAsProfileDialog(this, _dialogCoordinator, SelectedProfile);
    }

    public ICommand DeleteProfileCommand => new RelayCommand(p => DeleteProfileAction(), ModifyProfile_CanExecute);

    private void DeleteProfileAction()
    {
        ProfileDialogManager.ShowDeleteProfileDialog(this, _dialogCoordinator, new List<ProfileInfo> { SelectedProfile });
    }

    public ICommand EditGroupCommand => new RelayCommand(EditGroupAction);

    private void EditGroupAction(object group)
    {
        ProfileDialogManager.ShowEditGroupDialog(this, _dialogCoordinator, ProfileManager.GetGroup(group.ToString()));
    }

    public ICommand ClearSearchCommand => new RelayCommand(p => ClearSearchAction());

    private void ClearSearchAction()
    {
        Search = string.Empty;
    }

    public ItemActionCallback CloseItemCommand => CloseItemAction;

    private static void CloseItemAction(ItemActionCallbackArgs<TabablzControl> args)
    {
        ((args.DragablzItem.Content as DragablzTabItem)?.View as RemoteDesktopControl)?.CloseTab();
    }
    #endregion

    #region Methods
    // Connect via Dialog
    private async Task Connect(string host = null)
    {
        var customDialog = new CustomDialog
        {
            Title = Localization.Resources.Strings.Connect
        };

        var remoteDesktopConnectViewModel = new RemoteDesktopConnectViewModel(async instance =>
        {
            await _dialogCoordinator.HideMetroDialogAsync(this, customDialog);
            ConfigurationManager.Current.IsDialogOpen = false;

            // Create new session info with default settings
            var sessionInfo = NETworkManager.Profiles.Application.RemoteDesktop.CreateSessionInfo();

            if(instance.Host.Contains(':'))
            {
                // Validate input via UI
                sessionInfo.Hostname = instance.Host.Split(':')[0];
                sessionInfo.Port = int.Parse(instance.Host.Split(':')[1]);
            }
            else
            {
                sessionInfo.Hostname = instance.Host;
            }

            if (instance.UseCredentials)
            {
                sessionInfo.UseCredentials = true;

                sessionInfo.Username = instance.Username;
                sessionInfo.Password = instance.Password;
            }

            // Add to history
            // Note: The history can only be updated after the values have been read.
            //       Otherwise, in some cases, incorrect values are taken over.
            AddHostToHistory(instance.Host);

            Connect(sessionInfo);
        }, async instance =>
        {
            await _dialogCoordinator.HideMetroDialogAsync(this, customDialog);
            ConfigurationManager.Current.IsDialogOpen = false;
        })
        {
            Host = host
        };

        customDialog.Content = new RemoteDesktopConnectDialog
        {
            DataContext = remoteDesktopConnectViewModel
        };

        ConfigurationManager.Current.IsDialogOpen = true;
        await _dialogCoordinator.ShowMetroDialogAsync(this, customDialog);
    }

    // Connect via Profile
    private void ConnectProfile()
    {
        var profileInfo = SelectedProfile;

        var sessionInfo = NETworkManager.Profiles.Application.RemoteDesktop.CreateSessionInfo(profileInfo);

        Connect(sessionInfo, profileInfo.Name);
    }

    // Connect via Profile with Credentials
    private async Task ConnectProfileAs()
    {
        var profileInfo = SelectedProfile;

        var sessionInfo = NETworkManager.Profiles.Application.RemoteDesktop.CreateSessionInfo(profileInfo);

        var customDialog = new CustomDialog
        {
            Title = Localization.Resources.Strings.ConnectAs
        };

        var remoteDesktopConnectViewModel = new RemoteDesktopConnectViewModel(async instance =>
        {
            await _dialogCoordinator.HideMetroDialogAsync(this, customDialog);
            ConfigurationManager.Current.IsDialogOpen = false;

            if (instance.UseCredentials)
            {
                sessionInfo.UseCredentials = true;
                sessionInfo.Username = instance.Username;
                sessionInfo.Password = instance.Password;
            }

            Connect(sessionInfo, instance.Name);
        }, async instance =>
        {
            await _dialogCoordinator.HideMetroDialogAsync(this, customDialog);
            ConfigurationManager.Current.IsDialogOpen = false;
        }, true)
        {
            // Set name, hostname
            Name = profileInfo.Name,
            Host = profileInfo.RemoteDesktop_Host,

            // Request credentials
            UseCredentials = true
        };

        customDialog.Content = new RemoteDesktopConnectDialog
        {
            DataContext = remoteDesktopConnectViewModel
        };

        ConfigurationManager.Current.IsDialogOpen = true;
        await _dialogCoordinator.ShowMetroDialogAsync(this, customDialog);
    }

    private void Connect(RemoteDesktopSessionInfo sessionInfo, string header = null)
    {
        TabItems.Add(new DragablzTabItem(header ?? sessionInfo.Hostname, new RemoteDesktopControl(sessionInfo)));
        SelectedTabIndex = TabItems.Count - 1;
    }

    public void AddTab(string host)
    {
        Connect(host);
    }

    // Modify history list
    private static void AddHostToHistory(string host)
    {
        if (string.IsNullOrEmpty(host))
            return;
        
        SettingsManager.Current.RemoteDesktop_HostHistory = new ObservableCollection<string>( ListHelper.Modify(SettingsManager.Current.RemoteDesktop_HostHistory.ToList(), host, SettingsManager.Current.General_HistoryListEntries));
    }
      
    private void ResizeProfile(bool dueToChangedSize)
    {
        _canProfileWidthChange = false;

        if (dueToChangedSize)
        {
            ExpandProfileView = Math.Abs(ProfileWidth.Value - GlobalStaticConfiguration.Profile_WidthCollapsed) > GlobalStaticConfiguration.Profile_FloatPointFix;
        }
        else
        {
            if (ExpandProfileView)
            {
                ProfileWidth = Math.Abs(_tempProfileWidth - GlobalStaticConfiguration.Profile_WidthCollapsed) < GlobalStaticConfiguration.Profile_FloatPointFix ? new GridLength(GlobalStaticConfiguration.Profile_DefaultWidthExpanded) : new GridLength(_tempProfileWidth);
            }
            else
            {
                _tempProfileWidth = ProfileWidth.Value;
                ProfileWidth = new GridLength(GlobalStaticConfiguration.Profile_WidthCollapsed);
            }
        }

        _canProfileWidthChange = true;
    }

    public void OnViewVisible()
    {
        _isViewActive = true;

        RefreshProfiles();
    }

    public void OnViewHide()
    {
        _isViewActive = false;
    }

    private void SetProfilesView(ProfileInfo profile = null)
    {
        Profiles = new CollectionViewSource { Source = ProfileManager.Groups.SelectMany(x => x.Profiles).Where(x => x.RemoteDesktop_Enabled).OrderBy(x => x.Group).ThenBy(x => x.Name) }.View;

        Profiles.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ProfileInfo.Group)));

        Profiles.Filter = o =>
        {
            if (o is not ProfileInfo info)
                return false;

            if (string.IsNullOrEmpty(Search))
                return true;

            var search = Search.Trim();

            // Search by: Tag=xxx (exact match, ignore case)
            /*
            if (search.StartsWith(ProfileManager.TagIdentifier, StringComparison.OrdinalIgnoreCase))
                return !string.IsNullOrEmpty(info.Tags) && info.PingMonitor_Enabled && info.Tags.Replace(" ", "").Split(';').Any(str => search.Substring(ProfileManager.TagIdentifier.Length, search.Length - ProfileManager.TagIdentifier.Length).Equals(str, StringComparison.OrdinalIgnoreCase));
            */

            // Search by: Name, RemoteDesktop_Host
            return info.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) > -1 || info.RemoteDesktop_Host.IndexOf(search, StringComparison.OrdinalIgnoreCase) > -1;
        };

        // Set specific profile or first if null
        SelectedProfile = null;

        if (profile != null)
            SelectedProfile = Profiles.Cast<ProfileInfo>().FirstOrDefault(x => x.Equals(profile)) ??
                Profiles.Cast<ProfileInfo>().FirstOrDefault();
        else
            SelectedProfile = Profiles.Cast<ProfileInfo>().FirstOrDefault();
    }

    public void RefreshProfiles()
    {
        if (!_isViewActive)
            return;

        SetProfilesView(SelectedProfile);
    }

    public void OnProfileManagerDialogOpen()
    {
        ConfigurationManager.Current.IsDialogOpen = true;
    }

    public void OnProfileManagerDialogClose()
    {
        ConfigurationManager.Current.IsDialogOpen = false;
    }
    #endregion

    #region Event
    private void ProfileManager_OnProfilesUpdated(object sender, EventArgs e)
    {
        RefreshProfiles();
    }

    private void SearchDispatcherTimer_Tick(object sender, EventArgs e)
    {
        _searchDispatcherTimer.Stop();

        RefreshProfiles();

        IsSearching = false;
    }
    #endregion
}
