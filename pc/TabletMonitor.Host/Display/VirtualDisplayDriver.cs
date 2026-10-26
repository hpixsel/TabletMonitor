using System.Diagnostics;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using TabletMonitor.Interop;

namespace TabletMonitor.Display;

/// <summary>
/// Bundled Indirect Display Driver (Virtual Display Driver by MikeTheTech, MIT, signed by SignPath Foundation).
/// The driver reads its list of modes from C:\VirtualDisplayDriver\vdd_settings.xml at start-up,
/// so a new mode requires restarting the device (admin).
/// </summary>
public static class VirtualDisplayDriver
{
    public const string HardwareId = @"Root\MttVDD";
    public const string ConfigDirectory = @"C:\VirtualDisplayDriver";
    public static readonly string SettingsPath = Path.Combine(ConfigDirectory, "vdd_settings.xml");

    /// <summary>Display refresh rates. The stream frame rate is separate (e.g. 30 fps on a 60 Hz display).</summary>
    public static readonly int[] RefreshRates = [60, 90, 120, 144];

    // Landscape panel resolutions of popular tablets and phones (portrait is done with display rotation),
    // most important first. Other panels are added on demand (AddedModesPath).
    private static readonly (int W, int H)[] KnownResolutions =
    [
        (2560, 1600), (1920, 1200), (1920, 1080), (2000, 1200), (2800, 1752), (2960, 1848), (2400, 1080), (2048, 1536),
    ];

    /// <summary>
    /// The driver fails to bring up its monitor (IddCxMonitorArrival → STATUS_NOT_SUPPORTED) when it exposes too
    /// many modes: 42 work, 105 do not. Each resolution yields <see cref="RefreshRates"/>.Length modes.
    /// </summary>
    private const int MaxModes = 60;

    /// <summary>Resolutions added for specific tablets, preserved across rewrites of the driver settings.</summary>
    private static readonly string AddedModesPath = Path.Combine(ConfigDirectory, "tabletmonitor_modes.txt");

    public static bool IsInstalled => SetupApi.FindDevices(HardwareId).Count > 0;

    /// <summary>
    /// Resolutions the driver should expose: tablet-specific ones first (newest first), then the known panels,
    /// each with its half-resolution variant, capped so the mode count stays within what the driver accepts.
    /// </summary>
    public static List<(int W, int H)> BuildResolutionList(IEnumerable<(int W, int H)> added)
    {
        var result = new List<(int, int)>();
        foreach (var (w, h) in added.Reverse().Concat(KnownResolutions))
        {
            foreach (var r in new[] { (Even(w), Even(h)), (Even(w / 2), Even(h / 2)) })
            {
                if (r.Item1 >= 640 && r.Item2 >= 360 && !result.Contains(r))
                    result.Add(r);
            }
        }
        return result.Take(MaxModes / RefreshRates.Length).ToList();
    }

    private static List<(int W, int H)> ReadAddedModes()
    {
        var list = new List<(int, int)>();
        try
        {
            foreach (var line in File.ReadAllLines(AddedModesPath))
            {
                var parts = line.Split('x');
                if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
                    list.Add((w, h));
            }
        }
        catch
        {
            // No tablet-specific modes yet.
        }
        return list;
    }

    public static bool SupportsResolution(int width, int height)
    {
        foreach (var (w, h) in ReadResolutions())
            if (w == width && h == height)
                return true;
        return false;
    }

    public static List<(int W, int H)> ReadResolutions()
    {
        var list = new List<(int, int)>();
        try
        {
            var doc = XDocument.Load(SettingsPath);
            foreach (var r in doc.Descendants("resolution"))
            {
                if (int.TryParse(r.Element("width")?.Value, out var w) && int.TryParse(r.Element("height")?.Value, out var h))
                    list.Add((w, h));
            }
        }
        catch
        {
            // Missing or broken file – treated as no modes.
        }
        return list;
    }

    /// <summary>Writes the driver configuration; <paramref name="extraResolutions"/> are remembered for later rewrites.</summary>
    public static void WriteSettings(IEnumerable<(int W, int H)> extraResolutions, bool logging = false)
    {
        Directory.CreateDirectory(ConfigDirectory);
        var added = ReadAddedModes().Concat(extraResolutions).Distinct().ToList();
        File.WriteAllLines(AddedModesPath, added.Select(r => $"{r.W}x{r.H}"));
        var resolutions = BuildResolutionList(added);

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement("vdd_settings",
                new XElement("monitors", new XElement("count", 1)),
                new XElement("gpu", new XElement("friendlyname", "default")),
                // Each resolution: its own entry at RefreshRates[0] plus the remaining rates as globals – no duplicates.
                new XElement("global", RefreshRates.Skip(1).Select(r => new XElement("g_refresh_rate", r))),
                new XElement("resolutions", resolutions.Select(r =>
                    new XElement("resolution",
                        new XElement("width", r.Item1),
                        new XElement("height", r.Item2),
                        new XElement("refresh_rate", RefreshRates[0])))),
                new XElement("options",
                    new XElement("CustomEdid", false),
                    new XElement("PreventSpoof", false),
                    new XElement("EdidCeaOverride", false),
                    new XElement("HardwareCursor", true),
                    new XElement("SDR10bit", false),
                    new XElement("HDRPlus", false),
                    new XElement("logging", logging),
                    new XElement("debuglogging", logging))));
        doc.Save(SettingsPath);
    }

    // ---------------- Elevated operations ----------------

    /// <summary>Installs the driver. Must run elevated.</summary>
    public static void Install(string driverDirectory)
    {
        WriteSettings([]);
        GrantUsersModify(ConfigDirectory);

        var inf = Path.Combine(driverDirectory, "MttVDD.inf");
        var dll = Path.Combine(driverDirectory, "MttVDD.dll");

        // Trust the publisher so Windows does not show an extra "install this device software?" dialog.
        try
        {
#pragma warning disable SYSLIB0057 // Authenticode signer extraction has no X509CertificateLoader equivalent
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(dll));
#pragma warning restore SYSLIB0057
            using var store = new X509Store(StoreName.TrustedPublisher, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            store.Add(cert);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not add driver publisher to TrustedPublisher: {ex.Message}");
        }

        if (!IsInstalled)
            SetupApi.CreateRootDevice(HardwareId);

        if (!SetupApi.UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, HardwareId, inf, SetupApi.INSTALLFLAG_FORCE, out _))
            throw new System.ComponentModel.Win32Exception();
    }

    /// <summary>
    /// Switches the virtual display adapter on or off. Off means its monitor is gone from Windows entirely
    /// (not just "disconnected" in display settings). Must run elevated – done by the service.
    /// </summary>
    public static void SetEnabled(bool enabled)
    {
        foreach (var id in SetupApi.FindDevices(HardwareId))
            RunPnpUtil($"{(enabled ? "/enable-device" : "/disable-device")} \"{id}\"");
    }

    /// <summary>Restarts the driver so it re-reads its settings. Must run elevated.</summary>
    public static void Restart()
    {
        foreach (var id in SetupApi.FindDevices(HardwareId))
            RunPnpUtil($"/restart-device \"{id}\"");
    }

    /// <summary>Removes the device and the driver package. Must run elevated.</summary>
    public static void Uninstall()
    {
        foreach (var id in SetupApi.FindDevices(HardwareId))
            RunPnpUtil($"/remove-device \"{id}\"");

        foreach (var oemInf in FindPublishedInfs("mttvdd.inf"))
            RunPnpUtil($"/delete-driver {oemInf} /uninstall");
    }

    private static IEnumerable<string> FindPublishedInfs(string originalName)
    {
        var output = RunPnpUtil("/enum-drivers");
        string? published = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var colon = line.IndexOf(':');
            if (colon < 0)
                continue;
            var value = line[(colon + 1)..].Trim();
            if (value.StartsWith("oem", StringComparison.OrdinalIgnoreCase) && value.EndsWith(".inf", StringComparison.OrdinalIgnoreCase))
                published = value;
            else if (published != null && value.Equals(originalName, StringComparison.OrdinalIgnoreCase))
                yield return published;
        }
    }

    private static string RunPnpUtil(string args)
    {
        var psi = new ProcessStartInfo("pnputil.exe", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Log.Info($"pnputil {args} -> {p.ExitCode}");
        return output;
    }

    private static void GrantUsersModify(string directory)
    {
        var info = new DirectoryInfo(directory);
        var acl = info.GetAccessControl();
        var users = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.AuthenticatedUserSid, null);
        acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(users,
            System.Security.AccessControl.FileSystemRights.Modify,
            System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
            System.Security.AccessControl.PropagationFlags.None,
            System.Security.AccessControl.AccessControlType.Allow));
        info.SetAccessControl(acl);
    }

    private static int Even(int v) => v & ~1;
}
