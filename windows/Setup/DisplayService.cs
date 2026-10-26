using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using TabletMonitor.Display;

namespace TabletMonitor.Setup;

/// <summary>
/// Tiny SYSTEM service ("TabletMonitor.exe --service") doing the few things that need admin rights, so the app
/// never has to show UAC during normal use: switching the virtual display adapter on/off (when it is off the
/// tablet screen disappears from Windows completely) and adding tablet resolutions to the driver.
/// </summary>
public sealed class DisplayService : ServiceBase
{
    public const string Name = "TabletMonitorService";
    public const string PipeName = "TabletMonitor.Service";

    private CancellationTokenSource? _cts;

    public DisplayService() => ServiceName = Name;

    protected override void OnStart(string[] args)
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ServeAsync(_cts.Token));
    }

    protected override void OnStop() => _cts?.Cancel();

    private static async Task ServeAsync(CancellationToken token)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
                await pipe.WaitForConnectionAsync(token);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                var command = await reader.ReadLineAsync(token) ?? "";
                await writer.WriteLineAsync(Handle(command.Trim()));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Error("Service pipe failed", ex);
                await Task.Delay(500, token);
            }
        }
    }

    /// <summary>Only a fixed set of commands with validated arguments is accepted.</summary>
    private static string Handle(string command)
    {
        try
        {
            var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (parts.FirstOrDefault()?.ToUpperInvariant())
            {
                case "PING":
                    return "OK";
                case "ENABLE":
                    VirtualDisplayDriver.SetEnabled(true);
                    return "OK";
                case "DISABLE":
                    VirtualDisplayDriver.SetEnabled(false);
                    return "OK";
                case "ADDMODE" when parts.Length == 2:
                    var wh = parts[1].Split('x');
                    if (wh.Length == 2 && int.TryParse(wh[0], out var w) && int.TryParse(wh[1], out var h) &&
                        w is >= 640 and <= 7680 && h is >= 360 and <= 4320)
                    {
                        VirtualDisplayDriver.WriteSettings([(w, h)]);
                        VirtualDisplayDriver.Restart();
                        return "OK";
                    }
                    return "ERR bad resolution";
                default:
                    return "ERR unknown command";
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Service command '{command}' failed", ex);
            return "ERR " + ex.Message;
        }
    }
}

/// <summary>Talks to <see cref="DisplayService"/>; every call degrades gracefully when the service is missing.</summary>
public static class ServiceClient
{
    /// <summary>The adapter is off between sessions – switch it on and wait for its display to appear.</summary>
    public static async Task<string?> EnsureVirtualDisplayAsync()
    {
        if (DisplayManager.FindVirtualDisplay() is { } existing)
            return existing;
        if (!await SendAsync("ENABLE"))
            return null;
        for (var i = 0; i < 50; i++)
        {
            if (DisplayManager.FindVirtualDisplay() is { } gdi)
                return gdi;
            await Task.Delay(100);
        }
        return null;
    }

    public static async Task<bool> SendAsync(string command, int timeoutMs = 10_000)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            await using var pipe = new NamedPipeClientStream(".", DisplayService.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(1000, cts.Token);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(command.AsMemory(), cts.Token);
            var reply = await reader.ReadLineAsync(cts.Token);
            if (reply != "OK")
                Log.Warn($"Service '{command}': {reply}");
            return reply == "OK";
        }
        catch (Exception ex) when (ex is System.TimeoutException or OperationCanceledException or IOException)
        {
            Log.Warn($"Service unavailable for '{command}': {ex.Message}");
            return false;
        }
    }
}
