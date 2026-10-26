using System.IO;
using System.Text.Json;

namespace TabletMonitor.Core;

public enum ResolutionChoice { Native, Half, Custom }
public enum Quality { Low, Balanced, High }
public enum OrientationChoice { Auto, Landscape, Portrait }

public sealed class DeviceSettings
{
    public string Name { get; set; } = "";
    public bool Trusted { get; set; }
    public bool AutoConnect { get; set; } = true;
    public ResolutionChoice Resolution { get; set; } = ResolutionChoice.Native;
    public int CustomWidth { get; set; }
    public int CustomHeight { get; set; }
    /// <summary>0 = automatic (60 Hz, or less if the tablet is slower).</summary>
    public int RefreshRate { get; set; }
    public Quality Quality { get; set; } = Quality.Balanced;
    /// <summary>0 = automatic, derived from the tablet's pixel density.</summary>
    public int ScalePercent { get; set; }
    public OrientationChoice Orientation { get; set; } = OrientationChoice.Auto;
    public int? PositionX { get; set; }
    public int? PositionY { get; set; }
}

public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TabletMonitor", "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public string PcId { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public bool TrayHintShown { get; set; }
    /// <summary>"auto" (follow Windows), "pl" or "en".</summary>
    public string Language { get; set; } = Localization.Strings.Auto;
    public Dictionary<string, DeviceSettings> Devices { get; set; } = new();

    public DeviceSettings For(string deviceId, string name)
    {
        if (!Devices.TryGetValue(deviceId, out var s))
            Devices[deviceId] = s = new DeviceSettings();
        s.Name = name;
        return s;
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings could not be read: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings could not be saved: {ex.Message}");
        }
    }
}
