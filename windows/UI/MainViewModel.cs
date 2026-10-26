using System.Collections.ObjectModel;
using System.Net;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TabletMonitor.Core;
using TabletMonitor.Display;
using TabletMonitor.Localization;
using TabletMonitor.Net;
using TabletMonitor.Setup;

namespace TabletMonitor.UI;

public sealed record Option<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>All application state and behaviour behind the main window and the tray icon.</summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan DeviceTimeout = TimeSpan.FromSeconds(7);

    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _sweepTimer;
    private readonly HashSet<string> _autoConnectSuppressed = new();
    private DiscoveryService? _discovery;
    private AdbBridge? _adb;
    private TabletSession? _session;
    private DeviceSettings? _sessionSettings;
    private bool _applyingSettings;

    public MainViewModel()
    {
        PcName = Environment.MachineName;
        _sweepTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RemoveStaleDevices(), _ui);
    }

    public string PcName { get; private set; }
    public string Version => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "";
    public ObservableCollection<DeviceViewModel> Devices { get; } = new();

    /// <summary>Raised for short notifications (shown in the tray when the window is hidden).</summary>
    public event Action<string, string>? Notify;

    // ---------------- Setup ----------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDeviceArea))]
    private bool _needsSetup;

    [ObservableProperty]
    private bool _isInstalling;

    /// <summary>The app is installed, but this exe is a different (newer) build.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SetupTitle), nameof(SetupText), nameof(SetupButton))]
    private bool _isUpdate;

    public string SetupTitle => IsUpdate ? Strings.SetupTitleUpdate : Strings.SetupTitleInstall;

    public string SetupText => IsUpdate ? Strings.SetupTextUpdate : Strings.SetupTextInstall;

    public string SetupButton => IsUpdate ? Strings.SetupButtonUpdate : Strings.SetupButtonInstall;

    [ObservableProperty]
    private string? _setupError;

    public bool ShowDeviceArea => !NeedsSetup;

    [ObservableProperty]
    private bool _startWithWindows = Installer.StartWithWindows;

    partial void OnStartWithWindowsChanged(bool value) => Installer.StartWithWindows = value;

    public bool CanSaveApk => Installer.HasResource("android/TabletMonitor.apk");

    public Option<string>[] LanguageOptions { get; } =
    [
        new(Strings.Auto, Strings.LanguageAuto),
        new(Strings.Polish, "Polski"),
        new(Strings.English, "English"),
    ];

    private Option<string>? _selectedLanguage;

    /// <summary>Changing the language restarts the app (texts are chosen once at start-up).</summary>
    public Option<string>? SelectedLanguage
    {
        get => _selectedLanguage ??= LanguageOptions.FirstOrDefault(o => o.Value == _settings.Language) ?? LanguageOptions[0];
        set
        {
            if (value == null || value.Value == _settings.Language)
                return;
            _selectedLanguage = value;
            _settings.Language = value.Value;
            _settings.Save();
            Installer.Restart();
        }
    }

    // ---------------- Updates ----------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateTitle))]
    private UpdateInfo? _availableUpdate;

    public string UpdateTitle => AvailableUpdate == null ? "" : Strings.UpdateAvailable(AvailableUpdate.Version.ToString(3));

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateNowCommand))]
    private bool _isUpdating;

    [ObservableProperty]
    private string _updateStatus = Strings.UpdateAvailableHint;

    public bool CheckForUpdates
    {
        get => _settings.CheckForUpdates;
        set
        {
            _settings.CheckForUpdates = value;
            _settings.Save();
            OnPropertyChanged();
            if (value)
                _ = CheckForUpdatesAsync();
        }
    }

    private DispatcherTimer? _updateTimer;

    private void StartUpdateChecks()
    {
        Installer.CleanUpAfterUpdate();
        _updateTimer = new DispatcherTimer(TimeSpan.FromHours(12), DispatcherPriority.Background, (_, _) => _ = CheckForUpdatesAsync(), _ui);
        _updateTimer.Start();
        _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ => _ui.BeginInvoke(() => _ = CheckForUpdatesAsync()));
    }

    private async Task CheckForUpdatesAsync()
    {
        if (!_settings.CheckForUpdates || IsUpdating)
            return;
        try
        {
            var update = await UpdateChecker.CheckAsync();
            if (update == null || update.Version == AvailableUpdate?.Version)
                return;
            AvailableUpdate = update;
            UpdateStatus = Strings.UpdateAvailableHint;
            Notify?.Invoke(UpdateTitle, Strings.UpdateAvailableHint);
        }
        catch (Exception ex)
        {
            Log.Warn($"Update check failed: {ex.Message}"); // offline etc. – try again later
        }
    }

    private bool CanUpdateNow() => !IsUpdating;

    [RelayCommand(CanExecute = nameof(CanUpdateNow))]
    private async Task UpdateNowAsync()
    {
        if (AvailableUpdate is not { } update)
            return;
        IsUpdating = true;
        try
        {
            UpdateStatus = Strings.Downloading(0);
            var progress = new Progress<double>(p => UpdateStatus = Strings.Downloading((int)(p * 100)));
            var exe = await UpdateChecker.DownloadAsync(update, progress);
            UpdateStatus = Strings.InstallingUpdate;
            if (!await Installer.InstallUpdateAsync(exe))
                UpdateStatus = Strings.SetupNeedsAdmin;
        }
        catch (Exception ex)
        {
            Log.Error("Update failed", ex);
            UpdateStatus = Strings.UpdateFailed(ex.Message);
        }
        finally
        {
            IsUpdating = false;
        }
    }

    [RelayCommand]
    private void OpenReleaseNotes()
    {
        if (AvailableUpdate != null)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AvailableUpdate.ReleaseUrl) { UseShellExecute = true });
    }

    public bool TrayHintShown
    {
        get => _settings.TrayHintShown;
        set
        {
            _settings.TrayHintShown = value;
            _settings.Save();
        }
    }

    // ---------------- Session ----------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(HasNoDevices), nameof(StatusText))]
    private DeviceViewModel? _activeDevice;

    public bool IsConnected => ActiveDevice?.State == DeviceState.Connected;

    public bool HasNoDevices => Devices.Count == 0 && ActiveDevice == null;

    public string StatusText => ActiveDevice switch
    {
        { State: DeviceState.Connected } d => Strings.StatusConnected(d.DisplayName),
        { State: DeviceState.Connecting } d => Strings.StatusConnecting(d.DisplayName),
        _ => Strings.StatusNotConnected,
    };

    [ObservableProperty] private string _statResolution = "";
    [ObservableProperty] private string _statFps = "";
    [ObservableProperty] private string _statBitrate = "";
    [ObservableProperty] private string _statLatency = "";
    [ObservableProperty] private string _statEncoder = "";

    [ObservableProperty]
    private string? _pendingRequestName;

    private DeviceViewModel? _pendingRequest;

    // Per-device settings of the active tablet
    public ObservableCollection<Option<ResolutionChoice>> ResolutionOptions { get; } = new();
    public ObservableCollection<Option<int>> RefreshOptions { get; } = new();
    public ObservableCollection<Option<int>> ScaleOptions { get; } = new();

    public Option<Quality>[] QualityOptions { get; } =
    [
        new(Quality.Low, Strings.QualityLow),
        new(Quality.Balanced, Strings.QualityBalanced),
        new(Quality.High, Strings.QualityHigh),
    ];

    public Option<OrientationChoice>[] OrientationOptions { get; } =
    [
        new(OrientationChoice.Auto, Strings.OrientationAuto),
        new(OrientationChoice.Landscape, Strings.OrientationLandscape),
        new(OrientationChoice.Portrait, Strings.OrientationPortrait),
    ];

    [ObservableProperty] private Option<ResolutionChoice>? _selectedResolution;
    [ObservableProperty] private Option<int>? _selectedRefresh;
    [ObservableProperty] private Option<int>? _selectedScale;
    [ObservableProperty] private Option<Quality>? _selectedQuality;
    [ObservableProperty] private Option<OrientationChoice>? _selectedOrientation;
    [ObservableProperty] private bool _autoConnect;

    partial void OnSelectedResolutionChanged(Option<ResolutionChoice>? value) => ApplySetting(s => s.Resolution = value?.Value ?? s.Resolution);
    partial void OnSelectedRefreshChanged(Option<int>? value) => ApplySetting(s => s.RefreshRate = value?.Value ?? s.RefreshRate);
    partial void OnSelectedScaleChanged(Option<int>? value) => ApplySetting(s => s.ScalePercent = value?.Value ?? s.ScalePercent);
    partial void OnSelectedQualityChanged(Option<Quality>? value) => ApplySetting(s => s.Quality = value?.Value ?? s.Quality);
    partial void OnSelectedOrientationChanged(Option<OrientationChoice>? value) => ApplySetting(s => s.Orientation = value?.Value ?? s.Orientation);

    partial void OnAutoConnectChanged(bool value)
    {
        if (_applyingSettings || _sessionSettings == null)
            return;
        _sessionSettings.AutoConnect = value;
        _settings.Save();
    }

    // ---------------- Start / stop ----------------

    public void Start()
    {
        IsUpdate = VirtualDisplayDriver.IsInstalled && Installer.IsNewerThanInstalled;
        NeedsSetup = !VirtualDisplayDriver.IsInstalled || IsUpdate;

        // A crash or power loss could have left the tablet screen attached – never leave it hanging.
        if (DisplayManager.FindVirtualDisplay() is { } gdi && DisplayManager.IsAttached(gdi))
            DisplayManager.Detach(gdi);
        // …and switch the adapter off so it does not linger in display settings either.
        if (!NeedsSetup)
            _ = ServiceClient.SendAsync("DISABLE");

        // Before setup the firewall rule does not exist yet – listening now would pop up a Windows dialog.
        if (!NeedsSetup)
        {
            StartServices();
            StartUpdateChecks();
        }
    }

    private void StartServices()
    {
        if (_discovery != null)
            return;
        try
        {
            _discovery = new DiscoveryService(_settings.PcId, PcName);
            _discovery.DeviceSeen += (info, ip) => _ui.BeginInvoke(() => OnWifiDevice(info, ip, requested: false));
            _discovery.ConnectRequested += (info, ip) => _ui.BeginInvoke(() => OnWifiDevice(info, ip, requested: true));
        }
        catch (Exception ex)
        {
            Log.Error("Discovery could not start", ex);
        }

        if (AdbBridge.IsAvailable)
        {
            try
            {
                _adb = new AdbBridge();
                _adb.DevicesChanged += list => _ui.BeginInvoke(() => OnUsbDevices(list));
            }
            catch (Exception ex)
            {
                Log.Error("adb could not start", ex);
            }
        }

        _sweepTimer.Start();
    }

    public void Dispose()
    {
        _sweepTimer.Stop();
        _updateTimer?.Stop();
        EndSession(null);
        _discovery?.Dispose();
        _adb?.Dispose();
        _settings.Save();
    }

    // ---------------- Setup commands ----------------

    [RelayCommand]
    private async Task InstallAsync()
    {
        IsInstalling = true;
        SetupError = null;
        try
        {
            // Development builds (not single-file) only install the driver.
            var singleFile = Installer.IsSingleFile;
            var ok = singleFile ? await Installer.InstallAsync() : await Installer.RunElevatedAsync("install-driver");
            if (!ok)
            {
                SetupError = Strings.SetupNeedsAdmin;
                return;
            }
            if (singleFile && !Installer.IsRunningInstalled)
            {
                Installer.StartWithWindows = true;
                Installer.LaunchInstalledAndExit();
                return;
            }
            NeedsSetup = !VirtualDisplayDriver.IsInstalled;
            if (!NeedsSetup)
            {
                await DetachWhenItAppearsAsync();
                StartServices();
            }
        }
        catch (Exception ex)
        {
            SetupError = ex.Message;
        }
        finally
        {
            IsInstalling = false;
        }
    }

    /// <summary>A freshly installed driver extends the desktop on its own – hide it until a tablet connects.</summary>
    private static async Task DetachWhenItAppearsAsync()
    {
        for (var i = 0; i < 50; i++)
        {
            if (DisplayManager.FindVirtualDisplay() is { } gdi && DisplayManager.IsAttached(gdi))
            {
                DisplayManager.Detach(gdi);
                return;
            }
            await Task.Delay(100);
        }
    }

    [RelayCommand]
    private void SaveApk()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = "TabletMonitor.apk",
            Filter = Strings.SaveApkFilter,
            Title = Strings.SaveApkTitle,
        };
        if (dialog.ShowDialog() != true)
            return;
        using var src = typeof(App).Assembly.GetManifestResourceStream("android/TabletMonitor.apk")!;
        using var dst = System.IO.File.Create(dialog.FileName);
        src.CopyTo(dst);
    }

    [RelayCommand]
    private void OpenLogs() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Log.Directory) { UseShellExecute = true });

    [RelayCommand]
    private void Refresh() => _discovery?.ProbeNow();

    // ---------------- Device list ----------------

    private void OnWifiDevice(DeviceInfo info, IPAddress ip, bool requested)
    {
        var device = Devices.FirstOrDefault(d => d.Id == info.Id) ?? (ActiveDevice?.Id == info.Id ? ActiveDevice : null);
        if (device == null)
        {
            device = new DeviceViewModel(info.Id, Transport.WiFi);
            Devices.Add(device);
            OnPropertyChanged(nameof(HasNoDevices));
        }
        device.Info = info;
        device.Address = ip;
        device.LastSeen = DateTime.UtcNow;

        if (device.State is DeviceState.Connected or DeviceState.Connecting)
            return;

        var known = _settings.Devices.TryGetValue(info.Id, out var s) ? s : null;
        if (requested)
        {
            _autoConnectSuppressed.Remove(info.Id);
            if (known?.Trusted == true)
                _ = ConnectAsync(device);
            else
                AskForApproval(device);
        }
        else if (known is { Trusted: true, AutoConnect: true } && _session == null && !info.Busy &&
                 !_autoConnectSuppressed.Contains(info.Id) && !NeedsSetup)
        {
            _ = ConnectAsync(device);
        }
    }

    private void OnUsbDevices(IReadOnlyList<UsbDevice> list)
    {
        foreach (var gone in Devices.Where(d => d.Transport == Transport.Usb && list.All(u => u.Serial != d.UsbSerial)).ToList())
            Devices.Remove(gone);

        foreach (var usb in list)
        {
            var device = Devices.FirstOrDefault(d => d.UsbSerial == usb.Serial);
            if (device == null)
            {
                device = new DeviceViewModel(usb.Info.Id, Transport.Usb) { UsbSerial = usb.Serial };
                Devices.Insert(0, device);
            }
            device.Info = usb.Info;
            device.LastSeen = DateTime.UtcNow;
            if (device.State is not (DeviceState.Connected or DeviceState.Connecting or DeviceState.InstallingApp))
            {
                device.State = usb.State switch
                {
                    UsbDeviceState.AppMissing => DeviceState.AppMissing,
                    UsbDeviceState.Unauthorized => DeviceState.Unauthorized,
                    _ => DeviceState.Available,
                };
            }
        }
        OnPropertyChanged(nameof(HasNoDevices));
    }

    private void RemoveStaleDevices()
    {
        var now = DateTime.UtcNow;
        foreach (var d in Devices.Where(d => d.Transport == Transport.WiFi && d.State == DeviceState.Available && now - d.LastSeen > DeviceTimeout).ToList())
        {
            Devices.Remove(d);
            _autoConnectSuppressed.Remove(d.Id); // it left – next time it may auto-connect again
        }
        OnPropertyChanged(nameof(HasNoDevices));
    }

    private void AskForApproval(DeviceViewModel device)
    {
        _pendingRequest = device;
        PendingRequestName = device.DisplayName;
        Notify?.Invoke(Strings.ConnectionRequest, Strings.ConnectionRequestText(device.DisplayName));
    }

    [RelayCommand]
    private async Task ApproveRequestAsync()
    {
        var device = _pendingRequest;
        _pendingRequest = null;
        PendingRequestName = null;
        if (device != null)
            await ConnectAsync(device);
    }

    [RelayCommand]
    private void RejectRequest()
    {
        _pendingRequest = null;
        PendingRequestName = null;
    }

    // ---------------- Connecting ----------------

    [RelayCommand]
    private async Task DeviceActionAsync(DeviceViewModel device)
    {
        if (device.State == DeviceState.AppMissing && device.UsbSerial != null && _adb != null)
        {
            device.State = DeviceState.InstallingApp;
            try
            {
                await _adb.InstallAppAsync(device.UsbSerial);
                device.State = DeviceState.Available;
                await ConnectAsync(device);
            }
            catch (Exception ex)
            {
                device.State = DeviceState.AppMissing;
                Notify?.Invoke(Strings.AppInstallFailed, ex.Message);
            }
            return;
        }
        await ConnectAsync(device);
    }

    public async Task ConnectAsync(DeviceViewModel device)
    {
        // Auto-connect and a request from the tablet often arrive together: only one attempt at a time,
        // and the device is marked as connecting before the first await.
        if (NeedsSetup || _connecting || device.State is DeviceState.Connecting or DeviceState.Connected)
            return;
        _connecting = true;
        try
        {
            await ConnectCoreAsync(device);
        }
        finally
        {
            _connecting = false;
        }
    }

    private bool _connecting;

    private async Task ConnectCoreAsync(DeviceViewModel device)
    {
        EndSession(null);
        _autoConnectSuppressed.Remove(device.Id);
        device.State = DeviceState.Connecting;
        ActiveDevice = device;
        Devices.Remove(device);

        var gdi = await ServiceClient.EnsureVirtualDisplayAsync();
        if (gdi == null)
        {
            device.State = DeviceState.Available;
            ActiveDevice = null;
            Devices.Insert(0, device);
            if (!VirtualDisplayDriver.IsInstalled)
                NeedsSetup = true;
            else
                Notify?.Invoke(Strings.ScreenStartFailed, Strings.ScreenStartFailedText);
            return;
        }

        var connection = new TabletConnection();
        try
        {
            string host;
            int port;
            if (device.Transport == Transport.Usb)
            {
                port = await _adb!.PrepareSessionAsync(device.UsbSerial!);
                host = "127.0.0.1";
            }
            else
            {
                host = device.Address!.ToString();
                port = device.Info.Port;
            }

            var info = await connection.ConnectAsync(host, port, PcName, TimeSpan.FromSeconds(8));
            if (device.Transport == Transport.Usb)
                info = info with { Id = device.Id };
            device.Info = info;

            var settings = _settings.For(info.Id, device.DisplayName);
            settings.Trusted = true;
            _settings.Save();

            var session = new TabletSession(connection, settings, gdi, EnsureResolutionAsync);
            session.Ended += reason => _ui.BeginInvoke(() =>
            {
                if (_session == session)
                    EndSession(reason);
                else
                    session.Dispose(); // never leave an orphaned session streaming
            });
            session.StatsUpdated += stats => _ui.BeginInvoke(() => ShowStats(stats));
            _session = session;
            _sessionSettings = settings;
            await session.StartAsync();

            device.State = DeviceState.Connected;
            LoadSessionSettings(device.Info, settings);
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(StatusText));
            Log.Info($"Connected to {device.DisplayName} via {device.TransportText}");
        }
        catch (Exception ex)
        {
            Log.Error($"Connecting to {device.DisplayName} failed", ex);
            if (_session != null)
                EndSession(null);
            else
                connection.Dispose();
            device.State = DeviceState.Available;
            ActiveDevice = null;
            Devices.Insert(0, device);
            Notify?.Invoke(Strings.ConnectFailed, FriendlyError(ex));
        }
    }

    [RelayCommand]
    private void Disconnect()
    {
        if (ActiveDevice != null)
            _autoConnectSuppressed.Add(ActiveDevice.Id); // the user turned it off – don't jump back on
        EndSession(null);
    }

    /// <summary>Stops streaming and removes the virtual screen from Windows.</summary>
    private void EndSession(string? reason)
    {
        var session = _session;
        _session = null;
        _sessionSettings = null;
        session?.Dispose();
        if (session != null)
            _ = ServiceClient.SendAsync("DISABLE"); // gone from display settings too, not just detached
        _settings.Save();

        var device = ActiveDevice;
        if (device != null)
        {
            device.State = DeviceState.Available;
            device.LastSeen = DateTime.UtcNow;
            ActiveDevice = null;
            if (!Devices.Contains(device))
                Devices.Insert(0, device);
            if (reason != null)
                Notify?.Invoke(Strings.TabletDisconnected, reason);
        }
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasNoDevices));
    }

    private async Task<bool> EnsureResolutionAsync(int width, int height)
    {
        try
        {
            // The service adds it silently; without the service fall back to a UAC prompt.
            if (!await ServiceClient.SendAsync($"ADDMODE {width}x{height}", 20_000))
            {
                Notify?.Invoke(Strings.NewResolution, Strings.NewResolutionText(width, height));
                if (!await Installer.AddResolutionAsync(width, height))
                    return false;
            }
            // The driver restarts; wait until its display shows up again.
            for (var i = 0; i < 50 && DisplayManager.FindVirtualDisplay() == null; i++)
                await Task.Delay(100);
            await Task.Delay(500);
            return VirtualDisplayDriver.SupportsResolution(width, height);
        }
        catch (Exception ex)
        {
            Log.Error("Adding resolution failed", ex);
            return false;
        }
    }

    private static string FriendlyError(Exception ex) => ex switch
    {
        TimeoutException or OperationCanceledException => Strings.ErrorTimeout,
        System.Net.Sockets.SocketException => Strings.ErrorNetwork,
        _ => ex.Message,
    };

    // ---------------- Active session view ----------------

    private void ShowStats(SessionStats stats)
    {
        StatResolution = stats.Resolution;
        StatFps = $"{stats.Fps:0}";
        StatBitrate = $"{stats.Mbps:0.0}";
        StatLatency = stats.LatencyMs > 0 ? $"{stats.LatencyMs}" : "–";
        StatEncoder = stats.Encoder;
    }

    private void LoadSessionSettings(DeviceInfo info, DeviceSettings s)
    {
        _applyingSettings = true;
        try
        {
            ResolutionOptions.Clear();
            ResolutionOptions.Add(new(ResolutionChoice.Native, Strings.ResolutionNative(info.Width, info.Height)));
            ResolutionOptions.Add(new(ResolutionChoice.Half, Strings.ResolutionHalf(info.Width / 2, info.Height / 2)));

            RefreshOptions.Clear();
            RefreshOptions.Add(new(0, Strings.RefreshAuto(Math.Min(info.Refresh, 60))));
            RefreshOptions.Add(new(30, Strings.Refresh30));
            foreach (var hz in VirtualDisplayDriver.RefreshRates.Where(hz => hz <= Math.Max(info.Refresh, 60)))
                RefreshOptions.Add(new(hz, $"{hz} Hz"));

            ScaleOptions.Clear();
            var auto = DisplayConfigScale(info);
            ScaleOptions.Add(new(0, Strings.ScaleAuto(auto)));
            foreach (var p in new[] { 100, 125, 150, 175, 200, 225, 250, 300 })
                ScaleOptions.Add(new(p, $"{p}%"));

            SelectedResolution = ResolutionOptions.FirstOrDefault(o => o.Value == s.Resolution) ?? ResolutionOptions[0];
            SelectedRefresh = RefreshOptions.FirstOrDefault(o => o.Value == s.RefreshRate) ?? RefreshOptions[0];
            SelectedScale = ScaleOptions.FirstOrDefault(o => o.Value == s.ScalePercent) ?? ScaleOptions[0];
            SelectedQuality = QualityOptions.First(o => o.Value == s.Quality);
            SelectedOrientation = OrientationOptions.First(o => o.Value == s.Orientation);
            AutoConnect = s.AutoConnect;
        }
        finally
        {
            _applyingSettings = false;
        }
    }

    /// <summary>Sample devices for "--screenshot" (README images); no services are started.</summary>
    internal void LoadDemo(bool connected)
    {
        PcName = "MY-PC"; // keep the real machine name out of published screenshots
        var tablet = new DeviceViewModel("demo-tablet", Transport.WiFi)
        {
            Info = new DeviceInfo { Name = "Galaxy Tab S9 FE+", Model = "Samsung SM-X610", Width = 2560, Height = 1600, Refresh = 90, Dpi = 320 },
        };
        var phone = new DeviceViewModel("demo-phone", Transport.Usb)
        {
            Info = new DeviceInfo { Name = "Pixel 8", Model = "Google Pixel 8", Width = 2400, Height = 1080, Refresh = 120, Dpi = 420 },
        };
        Devices.Add(phone);
        if (connected)
        {
            tablet.State = DeviceState.Connected;
            ActiveDevice = tablet;
            LoadSessionSettings(tablet.Info, new DeviceSettings());
            ShowStats(new SessionStats("2560×1600", 60, 14.2, 3, "AMDh264Encoder (GPU)"));
        }
        else
        {
            Devices.Insert(0, tablet);
        }
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasNoDevices));
    }

    private static int DisplayConfigScale(DeviceInfo info) =>
        Interop.DisplayConfig.ClosestStep(Math.Clamp((int)(info.Dpi / 160.0 * 100), 100, 300));

    private void ApplySetting(Action<DeviceSettings> change)
    {
        if (_applyingSettings || _sessionSettings == null || _session == null)
            return;
        change(_sessionSettings);
        _settings.Save();
        var session = _session;
        _ = Task.Run(async () =>
        {
            try
            {
                await session.ReapplyAsync();
            }
            catch (Exception ex)
            {
                Log.Error("Applying settings failed", ex);
                Notify?.Invoke(Strings.SettingsChangeFailed, ex.Message);
            }
        });
    }
}
