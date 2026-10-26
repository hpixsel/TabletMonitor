using System.Diagnostics;
using TabletMonitor.Display;
using TabletMonitor.Interop;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace TabletMonitor.Streaming;

public sealed record StreamSettings(int Fps, int Bitrate, double Scale);

/// <summary>
/// Capture → GPU colour conversion → H.264 loop for one display, on a dedicated thread.
/// Frames are produced only when the desktop changes, so a static screen costs almost nothing.
/// </summary>
public sealed class ScreenStreamer : IDisposable
{
    private readonly string _gdiName;
    private readonly Thread _thread;
    private readonly CursorTracker _cursor = new();
    private volatile bool _running = true;
    private volatile bool _keyFrameRequested = true;
    private volatile StreamSettings _settings;
    private volatile bool _settingsChanged;

    /// <summary>Returns false while the network is congested – capture pauses and frames coalesce.</summary>
    public Func<bool> CanSend { get; set; } = () => true;

    public event Action<int, int>? VideoSizeChanged;
    public event Action<EncodedFrame>? FrameEncoded;
    public event Action<CursorShape>? CursorShapeChanged;
    public event Action<int, int, bool>? CursorMoved;
    public event Action<string>? EncoderChanged;
    public event Action<Exception>? Error;

    public ScreenStreamer(string gdiName, StreamSettings settings)
    {
        _gdiName = gdiName;
        _settings = settings;
        _thread = new Thread(Run) { IsBackground = true, Name = "ScreenStreamer", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void RequestKeyFrame() => _keyFrameRequested = true;

    public void Update(StreamSettings settings)
    {
        _settings = settings;
        _settingsChanged = true;
    }

    private void Run()
    {
        User32.timeBeginPeriod(1);
        try
        {
            var failures = 0;
            while (_running)
            {
                try
                {
                    RunPipeline();
                    failures = 0;
                }
                catch (Exception ex)
                {
                    Log.Error("Streaming pipeline failed", ex);
                    if (++failures == 3)
                        Error?.Invoke(ex);
                    Thread.Sleep(Math.Min(2000, 200 * failures));
                }
            }
        }
        finally
        {
            User32.timeEndPeriod(1);
        }
    }

    /// <summary>Runs until the pipeline must be rebuilt (mode change, settings change, device lost) or stop.</summary>
    private void RunPipeline()
    {
        using var duplicator = new DesktopDuplicator(_gdiName);
        var settings = _settings;
        _settingsChanged = false;

        var portrait = duplicator.Rotation is ModeRotation.Rotate90 or ModeRotation.Rotate270;
        var (visibleW, visibleH) = portrait ? (duplicator.Height, duplicator.Width) : (duplicator.Width, duplicator.Height);
        var outW = Even((int)(visibleW * settings.Scale));
        var outH = Even((int)(visibleH * settings.Scale));
        var rotation = duplicator.Rotation switch
        {
            ModeRotation.Rotate90 => VideoProcessorRotation.Rotation90,
            ModeRotation.Rotate180 => VideoProcessorRotation.Rotation180,
            ModeRotation.Rotate270 => VideoProcessorRotation.Rotation270,
            _ => VideoProcessorRotation.Identity,
        };

        using var converter = new VideoConverter(duplicator.Device, duplicator.Context, duplicator.Frame!, outW, outH, rotation);
        using var encoder = new H264Encoder(duplicator.Device, duplicator.Context, duplicator.AdapterLuid, outW, outH, settings.Fps, settings.Bitrate);
        EncoderChanged?.Invoke($"{encoder.Name} ({(encoder.IsHardware ? "GPU" : "CPU")})");
        VideoSizeChanged?.Invoke(outW, outH);
        _cursor.Reset();
        _keyFrameRequested = true;

        var clock = Stopwatch.StartNew();
        var minInterval = TimeSpan.FromSeconds(1.0 / settings.Fps);
        var lastEncode = -minInterval;
        var lastCursorPoll = TimeSpan.Zero;
        var hasFrame = false;
        var dirty = false;
        void Emit(EncodedFrame f) => FrameEncoded?.Invoke(f);

        while (_running && !_settingsChanged)
        {
            encoder.Poll(Emit);

            var now = clock.Elapsed;
            if (now - lastCursorPoll >= TimeSpan.FromMilliseconds(4) && DisplayManager.GetBounds(_gdiName) is { } bounds)
            {
                lastCursorPoll = now;
                _cursor.Poll(bounds, outW, outH, s => CursorShapeChanged?.Invoke(s), (x, y, v) => CursorMoved?.Invoke(x, y, v));
            }

            if (!CanSend() || !encoder.CanAcceptInput)
            {
                Thread.Sleep(1);
                continue;
            }

            if (!dirty)
            {
                switch (duplicator.Acquire(1))
                {
                    case AcquireResult.NewFrame:
                        hasFrame = dirty = true;
                        break;
                    case AcquireResult.Lost:
                        // Mode change, rotation, secure desktop … – rebuild everything for the new geometry.
                        Thread.Sleep(50);
                        return;
                }
            }

            if (_keyFrameRequested && hasFrame)
                dirty = true;
            if (!dirty)
                continue;

            if (now - lastEncode < minInterval)
            {
                Thread.Sleep(1);
                continue;
            }

            if (_keyFrameRequested)
            {
                _keyFrameRequested = false;
                encoder.RequestKeyFrame();
            }
            var nv12 = converter.Convert();
            encoder.Encode(nv12, now.Ticks, Emit);
            lastEncode = now;
            dirty = false;
        }
    }

    private static int Even(int v) => Math.Max(2, v & ~1);

    public void Dispose()
    {
        _running = false;
        _thread.Join(3000);
    }
}
