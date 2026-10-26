using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice;
using Vortice.Mathematics;

namespace TabletMonitor.Streaming;

/// <summary>
/// BGRA → NV12 (BT.709, limited range) with optional scaling and rotation, done by the GPU video processor.
/// Output textures are pooled so the encoder can still read the previous frames.
/// </summary>
public sealed class VideoConverter : IDisposable
{
    private const int PoolSize = 4;

    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext1 _videoContext;
    private readonly ID3D11VideoProcessorEnumerator _enumerator;
    private readonly ID3D11VideoProcessor _processor;
    private readonly ID3D11VideoProcessorInputView _input;
    private readonly ID3D11Texture2D[] _outputs = new ID3D11Texture2D[PoolSize];
    private readonly ID3D11VideoProcessorOutputView[] _outputViews = new ID3D11VideoProcessorOutputView[PoolSize];
    private int _next;

    public int OutputWidth { get; }
    public int OutputHeight { get; }

    public VideoConverter(ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D source,
        int outputWidth, int outputHeight, VideoProcessorRotation rotation)
    {
        OutputWidth = outputWidth;
        OutputHeight = outputHeight;
        var src = source.Description;

        _videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = context.QueryInterface<ID3D11VideoContext1>();

        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational(60, 1),
            InputWidth = src.Width,
            InputHeight = src.Height,
            OutputFrameRate = new Rational(60, 1),
            OutputWidth = (uint)outputWidth,
            OutputHeight = (uint)outputHeight,
            Usage = VideoUsage.OptimalSpeed,
        };
        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(content);
        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);

        _input = _videoDevice.CreateVideoProcessorInputView(source, _enumerator, new VideoProcessorInputViewDescription
        {
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
        });

        for (var i = 0; i < PoolSize; i++)
        {
            _outputs[i] = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)outputWidth,
                Height = (uint)outputHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget,
            });
            _outputViews[i] = _videoDevice.CreateVideoProcessorOutputView(_outputs[i], _enumerator, new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
            });
        }

        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);
        _videoContext.VideoProcessorSetStreamColorSpace1(_processor, 0, ColorSpaceType.RgbFullG22NoneP709);
        _videoContext.VideoProcessorSetOutputColorSpace1(_processor, ColorSpaceType.YcbcrStudioG22LeftP709);
        if (rotation != VideoProcessorRotation.Identity)
            _videoContext.VideoProcessorSetStreamRotation(_processor, 0, true, rotation);

        var dst = new RawRect(0, 0, outputWidth, outputHeight);
        _videoContext.VideoProcessorSetStreamSourceRect(_processor, 0, true, new RawRect(0, 0, (int)src.Width, (int)src.Height));
        _videoContext.VideoProcessorSetStreamDestRect(_processor, 0, true, dst);
        _videoContext.VideoProcessorSetOutputTargetRect(_processor, true, dst);
    }

    /// <summary>Converts the current source texture and returns the NV12 texture holding the result.</summary>
    public ID3D11Texture2D Convert()
    {
        var index = _next;
        _next = (_next + 1) % PoolSize;
        var stream = new VideoProcessorStream { Enable = true, InputSurface = _input };
        _videoContext.VideoProcessorBlt(_processor, _outputViews[index], 0, 1, [stream]).CheckError();
        return _outputs[index];
    }

    public void Dispose()
    {
        foreach (var v in _outputViews) v?.Dispose();
        foreach (var t in _outputs) t?.Dispose();
        _input.Dispose();
        _processor.Dispose();
        _enumerator.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
    }
}
