using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using TabletMonitor.Setup;

namespace TabletMonitor.Net;

public enum UsbDeviceState { Ready, AppMissing, Unauthorized }

public sealed record UsbDevice(string Serial, string Model, UsbDeviceState State, DeviceInfo Info);

/// <summary>
/// USB connection through the bundled adb: detects tablets with USB debugging enabled, starts the app on
/// them, can install the bundled APK and forwards the session port to localhost.
/// </summary>
public sealed partial class AdbBridge : IDisposable
{
    public const string PackageName = "com.tabletmonitor.app";
    private const string Activity = PackageName + "/.MainActivity";

    private readonly string _adb;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, UsbDevice> _known = new();
    private readonly ConcurrentDictionary<string, int> _forwards = new();
    private bool _serverStarted;

    public event Action<IReadOnlyList<UsbDevice>>? DevicesChanged;

    public static bool IsAvailable => Installer.HasResource("adb/adb.exe");

    public AdbBridge()
    {
        var dir = Path.Combine(Log.Directory, "adb");
        Installer.ExtractResources("adb/", dir);
        _adb = Path.Combine(dir, "adb.exe");
        _ = Task.Run(PollLoopAsync);
    }

    private async Task PollLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        do
        {
            try
            {
                await RefreshAsync();
            }
            catch (Exception ex)
            {
                Log.Warn($"adb poll failed: {ex.Message}");
            }
        }
        while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false));
    }

    private async Task RefreshAsync()
    {
        if (!_serverStarted)
        {
            await RunAsync("start-server", TimeSpan.FromSeconds(15));
            _serverStarted = true;
        }

        var output = await RunAsync("devices", TimeSpan.FromSeconds(5));
        var current = new Dictionary<string, string>();
        foreach (var line in output.Split('\n').Skip(1))
        {
            var parts = line.Trim().Split('\t');
            if (parts.Length == 2)
                current[parts[0]] = parts[1];
        }

        var changed = false;
        foreach (var serial in _known.Keys.Except(current.Keys).ToList())
        {
            _known.TryRemove(serial, out _);
            _forwards.TryRemove(serial, out _);
            changed = true;
        }

        foreach (var (serial, status) in current)
        {
            var state = status switch
            {
                "device" => UsbDeviceState.Ready,
                "unauthorized" => UsbDeviceState.Unauthorized,
                _ => (UsbDeviceState?)null,
            };
            if (state == null)
                continue;

            // Re-probe only new devices or ones whose state may have changed.
            if (_known.TryGetValue(serial, out var known) && known.State == state)
                continue;

            var device = state == UsbDeviceState.Ready
                ? await ProbeAsync(serial)
                : new UsbDevice(serial, Localization.Strings.UsbDevice, UsbDeviceState.Unauthorized, new DeviceInfo { Id = "usb:" + serial, Name = Localization.Strings.UsbDevice });
            _known[serial] = device;
            changed = true;
        }

        if (changed)
            DevicesChanged?.Invoke(_known.Values.ToList());
    }

    private async Task<UsbDevice> ProbeAsync(string serial)
    {
        var model = (await ShellAsync(serial, "getprop ro.product.model")).Trim();
        var name = (await ShellAsync(serial, "settings get global device_name")).Trim();
        if (string.IsNullOrEmpty(name) || name == "null")
            name = model;
        var size = SizeRegex().Matches(await ShellAsync(serial, "wm size")).LastOrDefault();
        var density = DensityRegex().Matches(await ShellAsync(serial, "wm density")).LastOrDefault();
        var installed = (await ShellAsync(serial, $"pm list packages {PackageName}")).Contains(PackageName);

        int w = 0, h = 0, dpi = 160;
        if (size != null)
        {
            w = int.Parse(size.Groups[1].Value);
            h = int.Parse(size.Groups[2].Value);
        }
        if (density != null)
            dpi = int.Parse(density.Groups[1].Value);

        var info = new DeviceInfo
        {
            Id = "usb:" + serial,
            Name = name,
            Model = model,
            Width = Math.Max(w, h),
            Height = Math.Min(w, h),
            Dpi = dpi,
        };
        return new UsbDevice(serial, model, installed ? UsbDeviceState.Ready : UsbDeviceState.AppMissing, info);
    }

    /// <summary>Launches the app on the tablet and forwards the session port. Returns the local port.</summary>
    public async Task<int> PrepareSessionAsync(string serial)
    {
        await ShellAsync(serial, $"am start -n {Activity} --ez usb true");
        if (!_forwards.TryGetValue(serial, out var port))
        {
            var output = await RunAsync($"-s {serial} forward tcp:0 tcp:{Protocol.TcpPort}", TimeSpan.FromSeconds(5));
            port = int.Parse(output.Trim());
            _forwards[serial] = port;
        }
        // Give the app a moment to start listening.
        await Task.Delay(800);
        return port;
    }

    public async Task InstallAppAsync(string serial)
    {
        var apk = Path.Combine(Log.Directory, "TabletMonitor.apk");
        Installer.ExtractResources("android/", Log.Directory);
        var output = await RunAsync($"-s {serial} install -r \"{apk}\"", TimeSpan.FromMinutes(2));
        if (!output.Contains("Success"))
            throw new InvalidOperationException(Localization.Strings.AppInstallFailedDetail(output.Trim()));
        _known.TryRemove(serial, out _); // re-probe on next poll
    }

    private Task<string> ShellAsync(string serial, string command) =>
        RunAsync($"-s {serial} shell {command}", TimeSpan.FromSeconds(5));

    private async Task<string> RunAsync(string args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(_adb, args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        using var cts = new CancellationTokenSource(timeout);
        var stdout = p.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = p.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(); } catch { /* ignore */ }
            throw new TimeoutException($"adb {args} timed out");
        }
        return await stdout + await stderr;
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (_serverStarted)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(_adb, "kill-server") { CreateNoWindow = true, UseShellExecute = false });
                p?.WaitForExit(2000);
            }
            catch
            {
                // ignore
            }
        }
    }

    [GeneratedRegex(@"(\d+)x(\d+)")]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"density:\s*(\d+)")]
    private static partial Regex DensityRegex();
}
