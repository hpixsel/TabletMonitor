using System.Drawing;
using TabletMonitor.Display;
using TabletMonitor.Input;
using TabletMonitor.Interop;
using TabletMonitor.Net;
using TabletMonitor.Streaming;

namespace TabletMonitor.Core;

public sealed record SessionStats(string Resolution, double Fps, double Mbps, int LatencyMs, string Encoder);

/// <summary>
/// One active tablet: owns the virtual display while connected, streams it and injects touch.
/// Disposing the session always removes the display from the desktop.
/// </summary>
public sealed class TabletSession : IDisposable
{

    private readonly TabletConnection _connection;
    private readonly DeviceSettings _settings;
    private readonly string _gdiName;
    private readonly Func<int, int, Task<bool>> _ensureResolution;
    private readonly TouchInjector _injector = new();
    private readonly Timer _statsTimer;
    private ScreenStreamer? _streamer;
    private Rectangle _bounds;
    private bool _viewPortrait;
    private DisplayMode? _mode;
    private string _encoder = "";
    private int _framesThisSecond;
    private long _lastBytes;
    private int _latencyMs;
    private int _fps = 60;
    private int _disposed;

    public DeviceInfo Device { get; }
    public DisplayMode? Mode => _mode;

    public event Action<string>? Ended;
    public event Action<SessionStats>? StatsUpdated;

    public TabletSession(TabletConnection connection, DeviceSettings settings, string gdiName, Func<int, int, Task<bool>> ensureResolution)
    {
        _connection = connection;
        _settings = settings;
        _gdiName = gdiName;
        _ensureResolution = ensureResolution;
        Device = connection.Device!;
        _statsTimer = new Timer(_ => PublishStats(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public async Task StartAsync()
    {
        _connection.Closed += reason => Ended?.Invoke(reason);
        _connection.PointersReceived += p =>
        {
            if (_bounds.Width > 0)
                _injector.Inject(p, _bounds);
        };
        _connection.RoundTrip += rtt => _latencyMs = (int)rtt.TotalMilliseconds;
        _connection.ViewSizeChanged += OnViewSizeChanged;

        await ApplyDisplayModeAsync();

        _streamer = new ScreenStreamer(_gdiName, CreateStreamSettings()) { CanSend = () => _connection.CanSendVideo };
        _streamer.VideoSizeChanged += (w, h) =>
        {
            _bounds = DisplayManager.GetBounds(_gdiName) ?? _bounds;
            _connection.SendVideoConfig(w, h);
        };
        var framesSent = 0;
        _streamer.FrameEncoded += f =>
        {
            Interlocked.Increment(ref _framesThisSecond);
            if (++framesSent <= 3 || f.KeyFrame)
                Log.Info($"Frame #{framesSent} sent: {f.Data.Length} B{(f.KeyFrame ? ", keyframe" : "")}");
            _connection.SendFrame(f);
        };
        _connection.KeyFrameRequested += () => Log.Info("Tablet requested a keyframe");
        _streamer.CursorShapeChanged += _connection.SendCursorShape;
        _streamer.CursorMoved += _connection.SendCursorPosition;
        _streamer.EncoderChanged += e => _encoder = e;
        _streamer.Error += ex => Ended?.Invoke(Localization.Strings.StreamError(ex.Message));
        _connection.KeyFrameRequested += _streamer.RequestKeyFrame;

        _statsTimer.Change(1000, 1000);
    }

    /// <summary>Re-applies settings after the user changed them (resolution, refresh rate, quality …).</summary>
    public async Task ReapplyAsync()
    {
        var previous = _mode;
        await ApplyDisplayModeAsync();
        _streamer?.Update(CreateStreamSettings());
        if (previous == _mode)
            _streamer?.RequestKeyFrame();
    }

    private async Task ApplyDisplayModeAsync()
    {
        var mode = await ResolveModeAsync();
        Point? position = _settings.PositionX is int x && _settings.PositionY is int y ? new Point(x, y) : null;
        if (DisplayManager.IsAttached(_gdiName) && DisplayManager.GetBounds(_gdiName) is { } current)
            position = current.Location; // keep the arrangement while changing the mode

        // Right after the adapter is switched on its monitor needs a moment to arrive.
        var attached = false;
        for (var i = 0; i < 20 && !attached; i++)
        {
            attached = DisplayManager.Attach(_gdiName, mode, position) || (position != null && DisplayManager.Attach(_gdiName, mode, null));
            if (!attached)
                await Task.Delay(150);
        }

        // The display appears asynchronously.
        for (var i = 0; i < 30; i++)
        {
            if (DisplayManager.GetBounds(_gdiName) is { Width: > 0 } b)
            {
                _bounds = b;
                break;
            }
            await Task.Delay(100);
        }
        if (_bounds.Width == 0)
            throw new InvalidOperationException(Localization.Strings.ScreenNotEnabled);

        var scale = _settings.ScalePercent > 0 ? _settings.ScalePercent : AutoScale(mode);
        DisplayConfig.TrySetScale(_gdiName, scale);
        _mode = mode;
        Log.Info($"Display ready: {mode} at {_bounds}, scale {scale}%");
    }

    private async Task<DisplayMode> ResolveModeAsync()
    {
        int w, h;
        switch (_settings.Resolution)
        {
            case ResolutionChoice.Custom when _settings.CustomWidth > 0 && _settings.CustomHeight > 0:
                (w, h) = (_settings.CustomWidth, _settings.CustomHeight);
                break;
            case ResolutionChoice.Half:
                (w, h) = (Device.Width / 2, Device.Height / 2);
                break;
            default:
                (w, h) = (Device.Width, Device.Height);
                break;
        }
        (w, h) = (Math.Max(w, h) & ~1, Math.Min(w, h) & ~1);
        if (w < 640 || h < 360)
            (w, h) = (1920, 1080);

        if (!VirtualDisplayDriver.SupportsResolution(w, h) && !await _ensureResolution(w, h))
        {
            // Closest supported mode with a similar aspect ratio.
            var aspect = (double)w / h;
            var best = VirtualDisplayDriver.ReadResolutions()
                .OrderBy(r => Math.Abs((double)r.W / r.H - aspect) * 10 + Math.Abs(r.W - w) / 1000.0)
                .FirstOrDefault((1920, 1080));
            (w, h) = best;
        }

        // Stream frame rate as chosen; the display runs at the nearest supported rate not below 60 Hz.
        _fps = _settings.RefreshRate > 0 ? _settings.RefreshRate : Math.Min(Device.Refresh, 60);
        var hz = VirtualDisplayDriver.RefreshRates.Where(r => r <= Math.Max(_fps, 60)).DefaultIfEmpty(60).Max();
        var portrait = _settings.Orientation switch
        {
            OrientationChoice.Portrait => true,
            OrientationChoice.Landscape => false,
            _ => _viewPortrait,
        };
        return new DisplayMode(w, h, hz, portrait);
    }

    /// <summary>Android density 160 dpi ≈ Windows 100 %; e.g. a 320 dpi tablet gets 200 %.</summary>
    private int AutoScale(DisplayMode mode)
    {
        var factor = (double)Math.Max(mode.Width, mode.Height) / Math.Max(1, Device.Width);
        return DisplayConfig.ClosestStep(Math.Clamp((int)(Device.Dpi / 160.0 * 100 * factor), 100, 300));
    }

    private StreamSettings CreateStreamSettings()
    {
        var mode = _mode!;
        var bitsPerPixel = _settings.Quality switch
        {
            Quality.Low => 0.04,
            Quality.High => 0.12,
            _ => 0.07,
        };
        var bitrate = (int)Math.Clamp(mode.Width * mode.Height * _fps * bitsPerPixel, 3_000_000, 60_000_000);
        return new StreamSettings(_fps, bitrate, 1.0);
    }

    private void OnViewSizeChanged(int width, int height)
    {
        var portrait = height > width;
        if (portrait == _viewPortrait)
            return;
        _viewPortrait = portrait;
        if (_settings.Orientation == OrientationChoice.Auto && _streamer != null)
            _ = ReapplyAsync();
    }

    private void PublishStats()
    {
        // The user may rearrange displays in Windows settings at any time.
        _bounds = DisplayManager.GetBounds(_gdiName) ?? _bounds;

        var bytes = _connection.BytesSent;
        var mbps = (bytes - _lastBytes) * 8 / 1_000_000.0;
        _lastBytes = bytes;
        var fps = Interlocked.Exchange(ref _framesThisSecond, 0);
        var res = _mode == null ? "" : _mode.Portrait ? $"{_mode.Height}×{_mode.Width}" : $"{_mode.Width}×{_mode.Height}";
        StatsUpdated?.Invoke(new SessionStats(res, fps, mbps, _latencyMs, _encoder));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _statsTimer.Dispose();

        // Remember where the user placed the tablet in the display arrangement.
        if (DisplayManager.GetBounds(_gdiName) is { } b)
        {
            _settings.PositionX = b.X;
            _settings.PositionY = b.Y;
        }

        _streamer?.Dispose();
        _injector.Dispose();
        DisplayManager.Detach(_gdiName);
        _connection.Disconnect();
        _connection.Dispose();
    }
}
