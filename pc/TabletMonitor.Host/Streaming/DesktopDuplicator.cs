using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace TabletMonitor.Streaming;

public enum AcquireResult { NewFrame, NoChange, Lost }

/// <summary>
/// Captures one Windows output with DXGI Desktop Duplication. The frame is copied into
/// <see cref="Frame"/> (BGRA, unrotated output orientation) owned by this class.
/// </summary>
public sealed class DesktopDuplicator : IDisposable
{
    private static readonly Result WaitTimeout = new(unchecked((int)0x887A0027));

    private readonly IDXGIOutput1 _output;
    private IDXGIOutputDuplication? _duplication;

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public long AdapterLuid { get; }
    public string AdapterName { get; }
    public ID3D11Texture2D? Frame { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public ModeRotation Rotation { get; private set; }

    public DesktopDuplicator(string gdiName)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            using (adapter)
            {
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    if (!string.Equals(output.Description.DeviceName, gdiName, StringComparison.OrdinalIgnoreCase))
                    {
                        output.Dispose();
                        continue;
                    }

                    _output = output.QueryInterface<IDXGIOutput1>();
                    output.Dispose();
                    var desc = adapter.Description1;
                    AdapterLuid = desc.Luid;
                    AdapterName = desc.Description;

                    D3D11.D3D11CreateDevice(adapter, DriverType.Unknown,
                        DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                        [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
                        out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
                    Device = device!;
                    Context = context!;
                    using (var mt = Device.QueryInterface<ID3D11Multithread>())
                        mt.SetMultithreadProtected(true);

                    Log.Info($"Capturing {gdiName} on {AdapterName}");
                    CreateDuplication();
                    return;
                }
            }
        }
        throw new InvalidOperationException($"Output {gdiName} not found on any graphics adapter.");
    }

    private void CreateDuplication()
    {
        _duplication?.Dispose();
        _duplication = _output.DuplicateOutput(Device);
        var d = _duplication.Description;
        Rotation = d.Rotation;
        if (Frame == null || Width != (int)d.ModeDescription.Width || Height != (int)d.ModeDescription.Height)
        {
            Frame?.Dispose();
            Width = (int)d.ModeDescription.Width;
            Height = (int)d.ModeDescription.Height;
            Frame = Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)Width,
                Height = (uint)Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            });
        }
    }

    /// <summary>Waits up to <paramref name="timeoutMs"/> for a new desktop image.</summary>
    public AcquireResult Acquire(uint timeoutMs)
    {
        if (_duplication == null)
        {
            try { CreateDuplication(); }
            catch { return AcquireResult.Lost; }
        }

        var hr = _duplication!.AcquireNextFrame(timeoutMs, out var info, out var resource);
        if (hr == WaitTimeout)
            return AcquireResult.NoChange;
        if (hr.Failure)
        {
            // Access lost: mode change, UAC prompt, display detached …
            _duplication.Dispose();
            _duplication = null;
            return AcquireResult.Lost;
        }

        try
        {
            if (info.LastPresentTime == 0)
                return AcquireResult.NoChange; // only the mouse moved
            using var texture = resource!.QueryInterface<ID3D11Texture2D>();
            Context.CopyResource(Frame!, texture);
            return AcquireResult.NewFrame;
        }
        finally
        {
            resource?.Dispose();
            _duplication.ReleaseFrame();
        }
    }

    public void Dispose()
    {
        _duplication?.Dispose();
        Frame?.Dispose();
        _output.Dispose();
        Context.Dispose();
        Device.Dispose();
    }
}
