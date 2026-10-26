using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using TabletMonitor.Localization;
using TabletMonitor.Net;

namespace TabletMonitor.UI;

public enum Transport { WiFi, Usb }

public enum DeviceState { Available, Connecting, Connected, AppMissing, Unauthorized, InstallingApp }

/// <summary>A tablet visible in the list (found over Wi-Fi or USB).</summary>
public sealed partial class DeviceViewModel : ObservableObject
{
    public DeviceViewModel(string id, Transport transport)
    {
        Id = id;
        Transport = transport;
    }

    public string Id { get; }
    public Transport Transport { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName), nameof(Details), nameof(Glyph))]
    private DeviceInfo _info = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionText), nameof(IsBusy), nameof(StatusText), nameof(CanAct))]
    private DeviceState _state;

    public IPAddress? Address { get; set; }
    public string? UsbSerial { get; set; }
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    public string DisplayName => string.IsNullOrWhiteSpace(Info.Name) ? Info.Model : Info.Name;

    public string TransportText => Transport == Transport.Usb ? "USB" : "Wi-Fi";

    public string TransportGlyph => Transport == Transport.Usb ? "" : "";

    public string Details
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Info.Model) && Info.Model != DisplayName)
                parts.Add(Info.Model);
            if (Info.Width > 0)
                parts.Add($"{Info.Width}×{Info.Height}");
            if (Info.Refresh > 60)
                parts.Add($"{Info.Refresh} Hz");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Phone or tablet icon, based on the screen size in density-independent pixels.</summary>
    public string Glyph => Info.Dpi > 0 && Info.Height * 160 / Info.Dpi < 600 ? "" : "";

    public bool IsBusy => State is DeviceState.Connecting or DeviceState.InstallingApp;

    public bool CanAct => State is DeviceState.Available or DeviceState.AppMissing;

    public string ActionText => State switch
    {
        DeviceState.AppMissing => Strings.ActionInstallApp,
        DeviceState.InstallingApp => Strings.ActionInstalling,
        DeviceState.Connecting => Strings.ActionConnecting,
        DeviceState.Unauthorized => Strings.ActionConfirmOnTablet,
        _ => Strings.ActionConnect,
    };

    public string StatusText => State switch
    {
        DeviceState.AppMissing => Strings.DeviceAppMissing,
        DeviceState.Unauthorized => Strings.DeviceUnauthorized,
        _ => "",
    };
}
