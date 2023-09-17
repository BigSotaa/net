﻿using System.Net;
using System.Windows.Input;
using System.Windows;
using System;
using System.Collections;
using System.Collections.ObjectModel;
using NETworkManager.Settings;
using NETworkManager.Models.Network;
using System.Threading;
using NETworkManager.Utilities;
using System.Windows.Threading;
using System.ComponentModel;
using System.Windows.Data;
using System.Linq;
using Dragablz;
using MahApps.Metro.Controls;
using MahApps.Metro.Controls.Dialogs;
using NETworkManager.Controls;
using NETworkManager.Models.Export;
using NETworkManager.Views;
using NETworkManager.Models;
using NETworkManager.Models.EventSystem;
using System.Threading.Tasks;
using log4net;

namespace NETworkManager.ViewModels;

public class TracerouteViewModel : ViewModelBase
{
    #region Variables
    private static readonly ILog Log = LogManager.GetLogger(typeof(TracerouteViewModel));
    
    private readonly IDialogCoordinator _dialogCoordinator;
    private CancellationTokenSource _cancellationTokenSource;
    
    private readonly int _tabId;
    private bool _firstLoad = true;

    private string _host;

    public string Host
    {
        get => _host;
        set
        {
            if (value == _host)
                return;

            _host = value;
            OnPropertyChanged();
        }
    }

    public ICollectionView HostHistoryView { get; }

    private bool _isRunning;

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (value == _isRunning)
                return;

            _isRunning = value;
            OnPropertyChanged();
        }
    }

    private bool _cancelTrace;

    public bool CancelTrace
    {
        get => _cancelTrace;
        set
        {
            if (value == _cancelTrace)
                return;

            _cancelTrace = value;
            OnPropertyChanged();
        }
    }

    private ObservableCollection<TracerouteHopInfo> _results = new();

    public ObservableCollection<TracerouteHopInfo> Results
    {
        get => _results;
        set
        {
            if (Equals(value, _results))
                return;

            _results = value;
        }
    }

    public ICollectionView ResultsView { get; }

    private TracerouteHopInfo _selectedResult;

    public TracerouteHopInfo SelectedResult
    {
        get => _selectedResult;
        set
        {
            if (value == _selectedResult)
                return;

            _selectedResult = value;
            OnPropertyChanged();
        }
    }

    private IList _selectedResults = new ArrayList();

    public IList SelectedResults
    {
        get => _selectedResults;
        set
        {
            if (Equals(value, _selectedResults))
                return;

            _selectedResults = value;
            OnPropertyChanged();
        }
    }

    private bool _ipGeolocationIsRateLimitReached;

    private bool _isStatusMessageDisplayed;

    public bool IsStatusMessageDisplayed
    {
        get => _isStatusMessageDisplayed;
        set
        {
            if (value == _isStatusMessageDisplayed)
                return;

            _isStatusMessageDisplayed = value;
            OnPropertyChanged();
        }
    }

    private string _statusMessage;

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (value == _statusMessage)
                return;

            _statusMessage = value;
            OnPropertyChanged();
        }
    }

    #endregion

    #region Constructor, load settings

    public TracerouteViewModel(IDialogCoordinator instance, int tabId, string host)
    {
        _dialogCoordinator = instance;

        _tabId = tabId;
        Host = host;

        // Set collection view
        HostHistoryView = CollectionViewSource.GetDefaultView(SettingsManager.Current.Traceroute_HostHistory);

        // Result view
        ResultsView = CollectionViewSource.GetDefaultView(Results);
        ResultsView.SortDescriptions.Add(new SortDescription(nameof(TracerouteHopInfo.Hop),
            ListSortDirection.Ascending));

        LoadSettings();
    }

    public void OnLoaded()
    {
        if (!_firstLoad)
            return;

        if (!string.IsNullOrEmpty(Host))
            StartTrace().ConfigureAwait(false);

        _firstLoad = false;
    }

    private void LoadSettings()
    {
        
    }

    #endregion

    #region ICommands & Actions

    public ICommand TraceCommand => new RelayCommand(_ => TraceAction(), Trace_CanExecute);

    private bool Trace_CanExecute(object parameter) => Application.Current.MainWindow != null &&
                                                       !((MetroWindow)Application.Current.MainWindow)!.IsAnyDialogOpen;

    private void TraceAction()
    {
        if (IsRunning)
            StopTrace();
        else
            StartTrace().ConfigureAwait(false);
    }

    public ICommand RedirectDataToApplicationCommand => new RelayCommand(RedirectDataToApplicationAction);

    private void RedirectDataToApplicationAction(object name)
    {
        if (name is not ApplicationName applicationName)
            return;

        var host = !string.IsNullOrEmpty(SelectedResult.Hostname)
            ? SelectedResult.Hostname
            : SelectedResult.IPAddress.ToString();

        EventSystem.RedirectToApplication(applicationName, host);
    }

    public ICommand PerformDNSLookupCommand => new RelayCommand(PerformDNSLookupAction);

    private void PerformDNSLookupAction(object data)
    {
        EventSystem.RedirectToApplication(ApplicationName.DNSLookup, data.ToString());
    }

    public ICommand CopyTimeToClipboardCommand => new RelayCommand(CopyTimeToClipboardAction);

    private void CopyTimeToClipboardAction(object timeIdentifier)
    {
        var time = timeIdentifier switch
        {
            "1" => Ping.TimeToString(SelectedResult.Status1, SelectedResult.Time1),
            "2" => Ping.TimeToString(SelectedResult.Status2, SelectedResult.Time2),
            "3" => Ping.TimeToString(SelectedResult.Status3, SelectedResult.Time3),
            _ => "-/-"
        };

        ClipboardHelper.SetClipboard(time);
    }

    public ICommand ExportCommand => new RelayCommand(_ => ExportAction());

    private void ExportAction()
    {
        Export().ConfigureAwait(false);
    }

    #endregion

    #region Methods

    private void StopTrace()
    {
        CancelTrace = true;
        _cancellationTokenSource.Cancel();
    }

    private async Task StartTrace()
    {
        _ipGeolocationIsRateLimitReached = false;
        StatusMessage = string.Empty;
        IsStatusMessageDisplayed = false;
        IsRunning = true;

        Results.Clear();

        // Change the tab title (not nice, but it works)
        var window = Application.Current.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive);

        if (window != null)
        {
            foreach (var tabablzControl in VisualTreeHelper.FindVisualChildren<TabablzControl>(window))
            {
                tabablzControl.Items.OfType<DragablzTabItem>().First(x => x.Id == _tabId).Header = Host;
            }
        }

        _cancellationTokenSource = new CancellationTokenSource();

        // Try to parse the string into an IP-Address
        if (!IPAddress.TryParse(Host, out var ipAddress))
        {
            var dnsResult =
                await DNSClientHelper.ResolveAorAaaaAsync(Host,
                    SettingsManager.Current.Network_ResolveHostnamePreferIPv4);

            if (dnsResult.HasError)
            {
                DisplayStatusMessage(DNSClientHelper.FormatDNSClientResultError(Host, dnsResult));

                IsRunning = false;

                return;
            }

            ipAddress = dnsResult.Value;
        }

        try
        {
            var traceroute = new Traceroute(new TracerouteOptions(
                SettingsManager.Current.Traceroute_Timeout,
                new byte[SettingsManager.Current.Traceroute_Buffer],
                SettingsManager.Current.Traceroute_MaximumHops,
                true,
                SettingsManager.Current.Traceroute_ResolveHostname,
                SettingsManager.Current.Traceroute_CheckIPApiIPGeolocation
            ));

            traceroute.HopReceived += Traceroute_HopReceived;
            traceroute.TraceComplete += Traceroute_TraceComplete;
            traceroute.MaximumHopsReached += Traceroute_MaximumHopsReached;
            traceroute.TraceError += Traceroute_TraceError;
            traceroute.UserHasCanceled += Traceroute_UserHasCanceled;

            traceroute.TraceAsync(ipAddress, _cancellationTokenSource.Token);

            // Add the host to history
            AddHostToHistory(Host);
        }
        catch (Exception ex) // This will catch any exception
        {
            IsRunning = false;

            DisplayStatusMessage(ex.Message);
        }
    }

    private async Task Export()
    {
        var customDialog = new CustomDialog
        {
            Title = Localization.Resources.Strings.Export
        };

        var exportViewModel = new ExportViewModel(async instance =>
            {
                await _dialogCoordinator.HideMetroDialogAsync(this, customDialog);

                try
                {
                    ExportManager.Export(instance.FilePath, instance.FileType,
                        instance.ExportAll
                            ? Results
                            : SelectedResults.Cast<TracerouteHopInfo>().ToArray());
                }
                catch (Exception ex)
                {
                    var settings = AppearanceManager.MetroDialog;
                    settings.AffirmativeButtonText = Localization.Resources.Strings.OK;

                    await _dialogCoordinator.ShowMessageAsync(this, Localization.Resources.Strings.Error,
                        Localization.Resources.Strings.AnErrorOccurredWhileExportingTheData + Environment.NewLine +
                        Environment.NewLine + ex.Message, MessageDialogStyle.Affirmative, settings);
                }

                SettingsManager.Current.Traceroute_ExportFileType = instance.FileType;
                SettingsManager.Current.Traceroute_ExportFilePath = instance.FilePath;
            }, _ => { _dialogCoordinator.HideMetroDialogAsync(this, customDialog); },
            new[] { ExportFileType.Csv, ExportFileType.Xml, ExportFileType.Json }, true,
            SettingsManager.Current.Traceroute_ExportFileType, SettingsManager.Current.Traceroute_ExportFilePath
        );

        customDialog.Content = new ExportDialog
        {
            DataContext = exportViewModel
        };

        await _dialogCoordinator.ShowMetroDialogAsync(this, customDialog);
    }

    private void AddHostToHistory(string host)
    {
        // Create the new list
        var list = ListHelper.Modify(SettingsManager.Current.Traceroute_HostHistory.ToList(), host,
            SettingsManager.Current.General_HistoryListEntries);

        // Clear the old items
        SettingsManager.Current.Traceroute_HostHistory.Clear();
        OnPropertyChanged(nameof(Host)); // Raise property changed again, after the collection has been cleared

        // Fill with the new items
        list.ForEach(x => SettingsManager.Current.Traceroute_HostHistory.Add(x));
    }

    public void OnClose()
    {
        if (IsRunning)
            StopTrace();
    }

    #endregion

    #region Events

    private void Traceroute_HopReceived(object sender, TracerouteHopReceivedArgs e)
    {
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(delegate
        {
            // Check error
            if (e.Args.IPGeolocationResult.HasError)
            {
                Log.Error($"ip-api.com error: {e.Args.IPGeolocationResult.ErrorMessage}, error code: {e.Args.IPGeolocationResult.ErrorCode}");
                
                DisplayStatusMessage($"ip-api.com: {e.Args.IPGeolocationResult.ErrorMessage}");
            }

            // Check rate limit 
            if (!_ipGeolocationIsRateLimitReached && e.Args.IPGeolocationResult.RateLimitIsReached)
            {
                _ipGeolocationIsRateLimitReached = true;
                
                Log.Warn($"ip-api.com rate limit reached. Try again in {e.Args.IPGeolocationResult.RateLimitRemainingTime} seconds.");
                
                DisplayStatusMessage($"ip-api.com {string.Format(Localization.Resources.Strings.RateLimitReachedTryAgainInXSeconds, e.Args.IPGeolocationResult.RateLimitRemainingTime)}");
            }
            
            Results.Add(e.Args);
        }));
    }

    private void Traceroute_MaximumHopsReached(object sender, MaximumHopsReachedArgs e)
    {
        DisplayStatusMessage(string.Format(Localization.Resources.Strings.MaximumNumberOfHopsReached, e.Hops));
        IsRunning = false;
    }

    private void Traceroute_UserHasCanceled(object sender, EventArgs e)
    {
        DisplayStatusMessage(Localization.Resources.Strings.CanceledByUserMessage);
        CancelTrace = false;
        IsRunning = false;
    }

    private void Traceroute_TraceError(object sender, TracerouteErrorArgs e)
    {
        DisplayStatusMessage(e.ErrorMessage);
        IsRunning = false;
    }

    private void Traceroute_TraceComplete(object sender, EventArgs e)
    {
        IsRunning = false;
    }

    private void DisplayStatusMessage(string message)
    {
        if (!string.IsNullOrEmpty(StatusMessage))
            StatusMessage += Environment.NewLine;

        StatusMessage += message;
        IsStatusMessageDisplayed = true;
    }

    #endregion
}