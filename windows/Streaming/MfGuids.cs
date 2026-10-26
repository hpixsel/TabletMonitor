using System.Runtime.InteropServices;

namespace TabletMonitor.Streaming;

internal static class MfGuids
{
    public static readonly Guid MFT_CATEGORY_VIDEO_ENCODER = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_NV12 = new("3231564E-0000-0010-8000-00AA00389B71");

    public static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid MF_MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    public static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid MF_MT_MPEG_SEQUENCE_HEADER = new("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");
    public static readonly Guid MF_MT_YUV_MATRIX = new("3e23d450-2c75-4d25-a00e-b91670d12327");
    public static readonly Guid MF_MT_VIDEO_NOMINAL_RANGE = new("c21b8ee5-b956-4071-8daf-325edf5cab11");
    public static readonly Guid MF_MT_VIDEO_PRIMARIES = new("dbfbe4d7-0740-4ee0-8192-850ab0e21935");
    public static readonly Guid MF_MT_TRANSFER_FUNCTION = new("5fb0fce9-be5c-4935-a811-ec838f8eed93");

    public static readonly Guid MF_TRANSFORM_ASYNC = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");
    public static readonly Guid MF_TRANSFORM_ASYNC_UNLOCK = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
    public static readonly Guid MF_LOW_LATENCY = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid MFT_ENUM_ADAPTER_LUID = new("1d39518c-e220-4da8-a07f-ba172552d6b1");
    public static readonly Guid MFT_FRIENDLY_NAME_Attribute = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
    public static readonly Guid MFSampleExtension_CleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");

    public static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    public static readonly Guid IID_ICodecAPI = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");

    public static readonly Guid CODECAPI_AVLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid CODECAPI_AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid CODECAPI_AVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid CODECAPI_AVEncCommonQualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");
    public static readonly Guid CODECAPI_AVEncMPVGOPSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid CODECAPI_AVEncMPVDefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    public static readonly Guid CODECAPI_AVEncVideoForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");

    public const uint MFT_ENUM_FLAG_SYNCMFT = 0x1;
    public const uint MFT_ENUM_FLAG_ASYNCMFT = 0x2;
    public const uint MFT_ENUM_FLAG_HARDWARE = 0x4;
    public const uint MFT_ENUM_FLAG_SORTANDFILTER = 0x40;

    public const int MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x100;
    public const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    public const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
    public const int MF_E_NO_EVENTS_AVAILABLE = unchecked((int)0xC00D3E80);
    public const int MF_EVENT_FLAG_NO_WAIT = 1;

    public const int METransformNeedInput = 601;
    public const int METransformHaveOutput = 602;
}

/// <summary>Minimal ICodecAPI access (not projected by Vortice) through its vtable.</summary>
internal sealed unsafe class CodecApi : IDisposable
{
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public uint ulVal;
        [FieldOffset(8)] public short boolVal;
    }

    private const ushort VT_BOOL = 11;
    private const ushort VT_UI4 = 19;

    private IntPtr _ptr;

    private CodecApi(IntPtr ptr) => _ptr = ptr;

    public static CodecApi? From(IntPtr unknown)
    {
        var iid = MfGuids.IID_ICodecAPI;
        return Marshal.QueryInterface(unknown, in iid, out var ptr) == 0 ? new CodecApi(ptr) : null;
    }

    public bool Set(Guid api, uint value) => SetValue(api, new Variant { vt = VT_UI4, ulVal = value });

    public bool Set(Guid api, bool value) => SetValue(api, new Variant { vt = VT_BOOL, boolVal = value ? (short)-1 : (short)0 });

    private bool SetValue(Guid api, Variant value)
    {
        if (_ptr == IntPtr.Zero)
            return false;
        var vtbl = *(IntPtr**)_ptr;
        var setValue = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, Variant*, int>)vtbl[9];
        var hr = setValue(_ptr, &api, &value);
        if (hr != 0)
            Log.Info($"ICodecAPI::SetValue {api} -> 0x{hr:X8}");
        return hr == 0;
    }

    public void Dispose()
    {
        if (_ptr != IntPtr.Zero)
        {
            Marshal.Release(_ptr);
            _ptr = IntPtr.Zero;
        }
    }
}
