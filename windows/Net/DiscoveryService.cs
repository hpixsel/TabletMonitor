using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace TabletMonitor.Net;

/// <summary>
/// Finds tablets in the local network: broadcasts probes on every IPv4 interface and listens for
/// announcements. Probes also tell tablets that this PC exists, so they can ask it to connect.
/// </summary>
public sealed class DiscoveryService : IDisposable
{
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();
    private readonly byte[] _probe;

    /// <summary>A tablet answered or announced itself.</summary>
    public event Action<DeviceInfo, IPAddress>? DeviceSeen;

    /// <summary>The user tapped this PC on the tablet.</summary>
    public event Action<DeviceInfo, IPAddress>? ConnectRequested;

    public DiscoveryService(string pcId, string pcName)
    {
        _probe = Protocol.DiscoveryPacket(Protocol.DiscoveryProbe, new { id = pcId, name = pcName, version = Protocol.Version });
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, Protocol.DiscoveryPort));
        _udp.EnableBroadcast = true;

        _ = Task.Run(ReceiveLoopAsync);
        _ = Task.Run(ProbeLoopAsync);
    }

    /// <summary>Sends a probe right now (e.g. when the window is opened).</summary>
    public void ProbeNow()
    {
        foreach (var target in BroadcastAddresses())
        {
            try { _udp.Send(_probe, _probe.Length, new IPEndPoint(target, Protocol.DiscoveryPort)); }
            catch (SocketException) { /* interface went away */ }
        }
    }

    private async Task ProbeLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            ProbeNow();
        }
        while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false));
    }

    private async Task ReceiveLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try
            {
                packet = await _udp.ReceiveAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                continue; // ICMP port unreachable etc.
            }

            var data = packet.Buffer;
            if (data.Length < 6 || !data.AsSpan(0, 4).SequenceEqual(Protocol.DiscoveryMagic))
                continue;
            var type = data[4];
            if (type is not (Protocol.DiscoveryInfo or Protocol.DiscoveryConnectRequest))
                continue;

            DeviceInfo? info;
            try { info = JsonSerializer.Deserialize<DeviceInfo>(data.AsSpan(5), Protocol.Json); }
            catch (JsonException) { continue; }
            if (info == null || string.IsNullOrEmpty(info.Id))
                continue;

            if (type == Protocol.DiscoveryConnectRequest)
                ConnectRequested?.Invoke(info, packet.RemoteEndPoint.Address);
            else
                DeviceSeen?.Invoke(info, packet.RemoteEndPoint.Address);
        }
    }

    private static IEnumerable<IPAddress> BroadcastAddresses()
    {
        var result = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork || addr.IPv4Mask == null)
                    continue;
                var ip = addr.Address.GetAddressBytes();
                var mask = addr.IPv4Mask.GetAddressBytes();
                var broadcast = new byte[4];
                for (var i = 0; i < 4; i++)
                    broadcast[i] = (byte)(ip[i] | ~mask[i]);
                result.Add(new IPAddress(broadcast));
            }
        }
        return result;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp.Dispose();
    }
}
