using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Collections.Concurrent;
using TabletMonitor.Localization;
using TabletMonitor.Streaming;

namespace TabletMonitor.Net;

/// <summary>TCP session with one tablet. The PC is always the connecting side.</summary>
public sealed class TabletConnection : IDisposable
{
    private const int MaxQueuedVideoFrames = 2;

    private readonly TcpClient _client = new() { NoDelay = true, SendBufferSize = 256 * 1024, ReceiveBufferSize = 64 * 1024 };
    private readonly ConcurrentQueue<byte[]> _control = new();
    private readonly ConcurrentQueue<byte[]> _video = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private NetworkStream? _stream;
    private int _queuedVideo;
    private int _closed;
    private long _bytesSent;

    public event Action<PointerData[]>? PointersReceived;
    public event Action? KeyFrameRequested;
    public event Action<int, int>? ViewSizeChanged;
    public event Action<TimeSpan>? RoundTrip;
    public event Action<string>? Closed;

    public DeviceInfo? Device { get; private set; }
    public long BytesSent => Interlocked.Read(ref _bytesSent);

    /// <summary>False while video frames are still waiting to be written – the streamer then skips capture.</summary>
    public bool CanSendVideo => Volatile.Read(ref _queuedVideo) < MaxQueuedVideoFrames;

    /// <summary>Connects, exchanges HELLO and returns the tablet's info.</summary>
    public async Task<DeviceInfo> ConnectAsync(string host, int port, string pcName, TimeSpan timeout)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        await _client.ConnectAsync(host, port, timeoutCts.Token);
        _stream = _client.GetStream();

        var hello = Protocol.JsonFrame(Protocol.Hello, new { name = pcName, version = Protocol.Version });
        await _stream.WriteAsync(hello, timeoutCts.Token);

        var (type, payload) = await ReadMessageAsync(timeoutCts.Token);
        if (type != Protocol.Hello)
            throw new IOException("Unexpected handshake from the tablet.");
        Device = JsonSerializer.Deserialize<DeviceInfo>(payload, Protocol.Json) ?? throw new IOException("Niepoprawne dane tabletu.");

        _ = Task.Run(WriteLoopAsync);
        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(PingLoopAsync);
        return Device;
    }

    public void SendVideoConfig(int width, int height) =>
        Enqueue(Protocol.JsonFrame(Protocol.VideoConfig, new { width, height, codec = "h264" }));

    public void SendFrame(EncodedFrame frame)
    {
        var payload = new byte[9 + frame.Data.Length];
        payload[0] = (byte)(frame.KeyFrame ? 1 : 0);
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(1), frame.TimestampTicks / 10);
        frame.Data.CopyTo(payload, 9);
        Interlocked.Increment(ref _queuedVideo);
        _video.Enqueue(Protocol.Frame(Protocol.VideoFrame, payload));
        _signal.Release();
    }

    public void SendCursorShape(CursorShape shape)
    {
        var payload = new byte[8 + shape.Argb.Length * 4];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0), (ushort)shape.Width);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2), (ushort)shape.Height);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(4), (ushort)shape.HotX);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(6), (ushort)shape.HotY);
        for (var i = 0; i < shape.Argb.Length; i++)
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8 + i * 4), shape.Argb[i]);
        Enqueue(Protocol.Frame(Protocol.CursorShape, payload));
    }

    public void SendCursorPosition(int x, int y, bool visible)
    {
        Span<byte> payload = stackalloc byte[5];
        BinaryPrimitives.WriteInt16BigEndian(payload, (short)Math.Clamp(x, short.MinValue, short.MaxValue));
        BinaryPrimitives.WriteInt16BigEndian(payload[2..], (short)Math.Clamp(y, short.MinValue, short.MaxValue));
        payload[4] = (byte)(visible ? 1 : 0);
        Enqueue(Protocol.Frame(Protocol.CursorPos, payload));
    }

    private void Enqueue(byte[] message)
    {
        _control.Enqueue(message);
        _signal.Release();
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            var token = _cts.Token;
            while (!token.IsCancellationRequested)
            {
                // Control messages (cursor, config, pings) always go before video.
                await _signal.WaitAsync(token);
                if (_control.TryDequeue(out var msg))
                {
                    await _stream!.WriteAsync(msg, token);
                    Interlocked.Add(ref _bytesSent, msg.Length);
                }
                else if (_video.TryDequeue(out var frame))
                {
                    await _stream!.WriteAsync(frame, token);
                    Interlocked.Add(ref _bytesSent, frame.Length);
                    Interlocked.Decrement(ref _queuedVideo);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            Close(Strings.ReasonConnectionLost);
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            var token = _cts.Token;
            while (!token.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(6)); // tablet answers pings every second
                var (type, payload) = await ReadMessageAsync(timeout.Token);
                switch (type)
                {
                    case Protocol.Pointers:
                        PointersReceived?.Invoke(PointerParser.Parse(payload));
                        break;
                    case Protocol.RequestKeyFrame:
                        KeyFrameRequested?.Invoke();
                        break;
                    case Protocol.Pong when payload.Length >= 8:
                        var sent = BinaryPrimitives.ReadInt64BigEndian(payload);
                        RoundTrip?.Invoke(TimeSpan.FromTicks(DateTime.UtcNow.Ticks - sent));
                        break;
                    case Protocol.ViewSize when payload.Length >= 4:
                        ViewSizeChanged?.Invoke(BinaryPrimitives.ReadUInt16BigEndian(payload), BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(2)));
                        break;
                    case Protocol.Bye:
                        Close(Strings.ReasonTabletEnded);
                        return;
                }
            }
        }
        catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
        {
            Close(Strings.ReasonTabletNotResponding);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            Close(Strings.ReasonConnectionLost);
        }
    }

    private async Task PingLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var payload = new byte[8];
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                BinaryPrimitives.WriteInt64BigEndian(payload, DateTime.UtcNow.Ticks);
                Enqueue(Protocol.Frame(Protocol.Ping, payload));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<(byte Type, byte[] Payload)> ReadMessageAsync(CancellationToken token)
    {
        var header = new byte[5];
        await _stream!.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
        if (length is < 0 or > 16 * 1024 * 1024)
            throw new IOException("Invalid message length");
        var payload = new byte[length];
        await _stream.ReadExactlyAsync(payload, token);
        return (header[0], payload);
    }

    private void Close(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;
        Log.Info($"Connection closed: {reason}");
        _cts.Cancel();
        try { _client.Close(); } catch { /* ignore */ }
        Closed?.Invoke(reason);
    }

    /// <summary>Politely ends the session (tablet returns to its waiting screen).</summary>
    public void Disconnect()
    {
        if (_closed != 0 || _stream == null)
            return;
        Enqueue(Protocol.Frame(Protocol.Bye, []));
        SpinWait.SpinUntil(() => _control.IsEmpty || _closed != 0, 300);
        Thread.Sleep(30);
        Close(Strings.ReasonDisconnected);
    }

    public void Dispose()
    {
        Close(Strings.ReasonDisconnected);
        _cts.Dispose();
        _client.Dispose();
    }
}
