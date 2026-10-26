using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;
using static TabletMonitor.Streaming.MfGuids;

namespace TabletMonitor.Streaming;

public readonly record struct EncodedFrame(byte[] Data, bool KeyFrame, long TimestampTicks);

/// <summary>
/// Low-latency H.264 encoder on a Media Foundation transform. Prefers the GPU encoder of the adapter that
/// owns the captured display (AMD AMF / NVENC / Intel QSV exposed as hardware MFTs, fed with D3D11 textures);
/// falls back to the Microsoft software encoder with CPU readback.
/// </summary>
public sealed unsafe class H264Encoder : IDisposable
{
    private static bool _mfStarted;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly IMFTransform _transform;
    private readonly IMFDXGIDeviceManager? _deviceManager;
    private readonly CodecApi? _codecApi;
    private readonly IntPtr _eventGenerator;
    private readonly bool _async;
    private readonly bool _providesSamples;
    private readonly int _outputBufferSize;
    private ID3D11Texture2D? _staging;
    private byte[]? _sequenceHeader;
    private int _pendingInputs;
    private bool _forceKeyFrame = true;

    public string Name { get; }
    public bool IsHardware { get; }

    public H264Encoder(ID3D11Device device, ID3D11DeviceContext context, long adapterLuid, int width, int height, int fps, int bitrate)
    {
        _device = device;
        _context = context;
        _width = width;
        _height = height;
        _fps = fps;

        if (!_mfStarted)
        {
            MediaFactory.MFStartup(true).CheckError();
            _mfStarted = true;
        }

        IMFTransform? transform = null;
        string name = "";
        foreach (var hardware in new[] { true, false })
        {
            foreach (var activate in Enumerate(hardware, adapterLuid))
            {
                using (activate)
                {
                    try
                    {
                        name = activate.GetString(MFT_FRIENDLY_NAME_Attribute);
                    }
                    catch
                    {
                        name = hardware ? "Hardware H.264" : "Software H.264";
                    }
                    try
                    {
                        transform = activate.ActivateObject<IMFTransform>();
                        if (hardware)
                        {
                            _deviceManager = MediaFactory.MFCreateDXGIDeviceManager();
                            _deviceManager.ResetDevice(device).CheckError();
                        }
                        Configure(transform, hardware, bitrate, out _async);
                        IsHardware = hardware;
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Encoder '{name}' unusable: {ex.Message}");
                        transform?.Dispose();
                        transform = null;
                        _deviceManager?.Dispose();
                        _deviceManager = null;
                    }
                }
            }
            if (transform != null)
                break;
        }

        _transform = transform ?? throw new InvalidOperationException("No usable H.264 encoder found.");
        Name = name;
        _codecApi = CodecApi.From(_transform.NativePointer);

        var info = _transform.GetOutputStreamInfo(0);
        _providesSamples = (info.Flags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;
        _outputBufferSize = Math.Max(info.Size, width * height * 3 / 2);

        if (_async)
        {
            var iid = typeof(IMFMediaEventGenerator).GUID;
            Marshal.QueryInterface(_transform.NativePointer, in iid, out _eventGenerator);
        }

        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);
        Log.Info($"Encoder: {Name} ({(IsHardware ? "GPU" : "CPU")}, {(_async ? "async" : "sync")}) {width}x{height}@{fps} {bitrate / 1000} kbps");
    }

    private static List<IMFActivate> Enumerate(bool hardware, long adapterLuid)
    {
        var output = new RegisterTypeInfo { GuidMajorType = MFMediaType_Video, GuidSubtype = MFVideoFormat_H264 };
        var flags = MFT_ENUM_FLAG_SORTANDFILTER | (hardware ? MFT_ENUM_FLAG_HARDWARE : MFT_ENUM_FLAG_SYNCMFT);
        using var attributes = MediaFactory.MFCreateAttributes(1);
        if (hardware)
            attributes.Set(MFT_ENUM_ADAPTER_LUID, (ulong)adapterLuid);

        MediaFactory.MFTEnum2(MFT_CATEGORY_VIDEO_ENCODER, flags, null, output, attributes, out var array, out var count);
        var list = new List<IMFActivate>();
        for (var i = 0; i < count; i++)
            list.Add(new IMFActivate(Marshal.ReadIntPtr(array, i * IntPtr.Size)));
        if (array != IntPtr.Zero)
            Marshal.FreeCoTaskMem(array);
        return list;
    }

    private void Configure(IMFTransform t, bool hardware, int bitrate, out bool isAsync)
    {
        using var attributes = t.Attributes;
        isAsync = MediaFactory.MFGetAttributeUInt32(attributes, MF_TRANSFORM_ASYNC, 0) != 0;
        if (isAsync)
            attributes.Set(MF_TRANSFORM_ASYNC_UNLOCK, 1u);
        attributes.Set(MF_LOW_LATENCY, 1u);

        if (hardware)
            t.ProcessMessage(TMessageType.MessageSetD3DManager, (nuint)_deviceManager!.NativePointer);

        using (var api = CodecApi.From(t.NativePointer))
        {
            api?.Set(CODECAPI_AVLowLatencyMode, true);
            api?.Set(CODECAPI_AVEncCommonRateControlMode, 0u); // CBR – steady latency on Wi-Fi
            api?.Set(CODECAPI_AVEncCommonMeanBitRate, (uint)bitrate);
            api?.Set(CODECAPI_AVEncMPVDefaultBPictureCount, 0u);
            api?.Set(CODECAPI_AVEncMPVGOPSize, (uint)(_fps * 60)); // keyframes on demand, plus one per minute
            api?.Set(CODECAPI_AVEncCommonQualityVsSpeed, 50u);
        }

        using (var type = MediaFactory.MFCreateMediaType())
        {
            type.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
            type.Set(MF_MT_SUBTYPE, MFVideoFormat_H264);
            type.Set(MF_MT_AVG_BITRATE, (uint)bitrate);
            type.Set(MF_MT_FRAME_SIZE, MediaFactory.PackSize((uint)_width, (uint)_height));
            type.Set(MF_MT_FRAME_RATE, MediaFactory.PackRatio(_fps, 1));
            type.Set(MF_MT_PIXEL_ASPECT_RATIO, MediaFactory.PackRatio(1, 1));
            type.Set(MF_MT_INTERLACE_MODE, 2u); // progressive
            type.Set(MF_MT_MPEG2_PROFILE, 100u); // High
            type.Set(MF_MT_YUV_MATRIX, 1u); // BT.709
            type.Set(MF_MT_VIDEO_PRIMARIES, 2u); // BT.709
            type.Set(MF_MT_TRANSFER_FUNCTION, 5u); // 709
            type.Set(MF_MT_VIDEO_NOMINAL_RANGE, 2u); // 16-235
            t.SetOutputType(0, type, 0);
        }

        for (var i = 0; ; i++)
        {
            IMFMediaType type;
            try { type = t.GetInputAvailableType(0, i); }
            catch { throw new InvalidOperationException("The encoder does not accept NV12."); }
            using (type)
            {
                if (type.GetGUID(MF_MT_SUBTYPE) != MFVideoFormat_NV12)
                    continue;
                type.Set(MF_MT_FRAME_SIZE, MediaFactory.PackSize((uint)_width, (uint)_height));
                type.Set(MF_MT_FRAME_RATE, MediaFactory.PackRatio(_fps, 1));
                t.SetInputType(0, type, 0);
                return;
            }
        }
    }

    public void RequestKeyFrame() => _forceKeyFrame = true;

    public void SetBitrate(int bitrate) => _codecApi?.Set(CODECAPI_AVEncCommonMeanBitRate, (uint)bitrate);

    /// <summary>True when the encoder is ready for another frame (call <see cref="Poll"/> first).</summary>
    public bool CanAcceptInput => !_async || _pendingInputs > 0;

    /// <summary>Encodes an NV12 texture. Output arrives via <paramref name="onOutput"/> (possibly later, from <see cref="Poll"/>).</summary>
    public void Encode(ID3D11Texture2D nv12, long timestampTicks, Action<EncodedFrame> onOutput)
    {
        if (_forceKeyFrame)
        {
            _codecApi?.Set(CODECAPI_AVEncVideoForceKeyFrame, 1u);
            _forceKeyFrame = false;
        }

        using var sample = MediaFactory.MFCreateSample();
        using var buffer = IsHardware ? CreateGpuBuffer(nv12) : CreateCpuBuffer(nv12);
        sample.AddBuffer(buffer);
        sample.SampleTime = timestampTicks;
        sample.SampleDuration = 10_000_000 / _fps;

        _transform.ProcessInput(0, sample, 0);
        if (_async)
            _pendingInputs--;
        else
            DrainOutput(onOutput);
    }

    /// <summary>Processes encoder events: collects finished frames and input requests (async encoders).</summary>
    public void Poll(Action<EncodedFrame> onOutput)
    {
        if (_async)
            PumpEvents(onOutput);
    }

    private IMFMediaBuffer CreateGpuBuffer(ID3D11Texture2D texture)
    {
        var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(IID_ID3D11Texture2D, texture, 0, false);
        using var buffer2D = buffer.QueryInterface<IMF2DBuffer>();
        buffer.CurrentLength = buffer2D.ContiguousLength;
        return buffer;
    }

    private IMFMediaBuffer CreateCpuBuffer(ID3D11Texture2D texture)
    {
        _staging ??= _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_width,
            Height = (uint)_height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Vortice.DXGI.Format.NV12,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
        });
        _context.CopyResource(_staging, texture);

        var size = _width * _height * 3 / 2;
        var buffer = MediaFactory.MFCreateMemoryBuffer(size);
        buffer.Lock(out var dst, out _, out _);
        var map = _context.Map(_staging, 0, MapMode.Read);
        try
        {
            var src = (byte*)map.DataPointer;
            var d = (byte*)dst;
            for (var y = 0; y < _height * 3 / 2; y++)
                Buffer.MemoryCopy(src + y * map.RowPitch, d + y * _width, _width, _width);
        }
        finally
        {
            _context.Unmap(_staging, 0);
            buffer.Unlock();
        }
        buffer.CurrentLength = size;
        return buffer;
    }

    private void PumpEvents(Action<EncodedFrame> onOutput)
    {
        var vtbl = *(IntPtr**)_eventGenerator;
        var getEvent = (delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)vtbl[3];
        while (true)
        {
            IntPtr eventPtr;
            var hr = getEvent(_eventGenerator, MF_EVENT_FLAG_NO_WAIT, &eventPtr);
            if (hr != 0)
                return; // MF_E_NO_EVENTS_AVAILABLE
            using var ev = new IMFMediaEvent(eventPtr);
            switch ((int)ev.EventType)
            {
                case METransformNeedInput:
                    _pendingInputs++;
                    break;
                case METransformHaveOutput:
                    ProcessOutput(onOutput);
                    break;
            }
        }
    }

    private void DrainOutput(Action<EncodedFrame> onOutput)
    {
        while (ProcessOutput(onOutput)) { }
    }

    private bool ProcessOutput(Action<EncodedFrame> onOutput)
    {
        IMFSample? own = null;
        if (!_providesSamples)
        {
            own = MediaFactory.MFCreateSample();
            own.AddBuffer(MediaFactory.MFCreateMemoryBuffer(_outputBufferSize));
        }

        var output = new OutputDataBuffer { StreamID = 0, Sample = own! };
        var hr = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);
        output.Events?.Dispose();

        if (hr.Code == MF_E_TRANSFORM_STREAM_CHANGE)
        {
            using var type = _transform.GetOutputAvailableType(0, 0);
            _transform.SetOutputType(0, type, 0);
            own?.Dispose();
            return true;
        }
        if (hr.Failure)
        {
            own?.Dispose();
            return false;
        }

        var sample = output.Sample;
        try
        {
            using var buffer = sample.ConvertToContiguousBuffer();
            buffer.Lock(out var ptr, out _, out var length);
            var data = new byte[length];
            Marshal.Copy(ptr, data, 0, length);
            buffer.Unlock();

            // The bitstream is the truth: not every encoder marks IDR frames with MFSampleExtension_CleanPoint.
            var keyFrame = ContainsNal(data, 5) || MediaFactory.MFGetAttributeUInt32(sample, MFSampleExtension_CleanPoint, 0) != 0;

            if (keyFrame)
                data = EnsureParameterSets(data);
            onOutput(new EncodedFrame(data, keyFrame, sample.SampleTime));
        }
        finally
        {
            if (!ReferenceEquals(sample, own))
                sample?.Dispose();
            own?.Dispose();
        }
        return true;
    }

    /// <summary>Some encoders put SPS/PPS only into the media type; the decoder needs them in-band.</summary>
    private byte[] EnsureParameterSets(byte[] frame)
    {
        if (ContainsNal(frame, 7))
            return frame;
        if (_sequenceHeader == null)
        {
            try
            {
                using var type = _transform.GetOutputCurrentType(0);
                _sequenceHeader = type.GetBlob(MF_MT_MPEG_SEQUENCE_HEADER);
            }
            catch
            {
                return frame;
            }
        }
        return [.. _sequenceHeader, .. frame];
    }

    private static bool ContainsNal(ReadOnlySpan<byte> data, int nalType)
    {
        for (var i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1 && (data[i + 3] & 0x1F) == nalType)
                return true;
        }
        return false;
    }

    public void Dispose()
    {
        try
        {
            _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, 0);
            _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, 0);
        }
        catch
        {
            // Shutting down anyway.
        }
        if (_eventGenerator != IntPtr.Zero)
            Marshal.Release(_eventGenerator);
        _codecApi?.Dispose();
        _staging?.Dispose();
        _transform.Dispose();
        _deviceManager?.Dispose();
    }
}
