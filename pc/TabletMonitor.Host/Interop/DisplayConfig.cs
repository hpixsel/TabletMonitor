using System.Runtime.InteropServices;

namespace TabletMonitor.Interop;

/// <summary>
/// CCD (Connecting and Configuring Displays) API – used to find the adapter/target of a GDI display
/// and to set its DPI scaling (the latter via the same private call the Settings app uses).
/// </summary>
internal static class DisplayConfig
{
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_DPI_SCALE = -3;
    private const int DISPLAYCONFIG_DEVICE_INFO_SET_DPI_SCALE = -4;

    // Scale steps used by Windows, relative to the recommended value.
    private static readonly int[] DpiSteps = [100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500];

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public uint refreshRateNumerator;
        public uint refreshRateDenominator;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO { }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public int type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DPI_SCALE_GET
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public int minScaleRel;
        public int curScaleRel;
        public int maxScaleRel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DPI_SCALE_SET
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public int scaleRel;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DPI_SCALE_GET requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigSetDeviceInfo(ref DPI_SCALE_SET setPacket);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(uint numPathArrayElements, [In] DISPLAYCONFIG_PATH_INFO[] pathArray,
        uint numModeInfoArrayElements, [In] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, uint flags);

    private const uint QDC_ALL_PATHS = 0x1;
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
    private const uint DISPLAYCONFIG_PATH_ACTIVE = 0x1;
    private const uint DISPLAYCONFIG_PATH_MODE_IDX_INVALID = 0xFFFFFFFF;
    private const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20;
    private const uint SDC_APPLY = 0x80;
    private const uint SDC_SAVE_TO_DATABASE = 0x200;
    private const uint SDC_ALLOW_CHANGES = 0x400;

    /// <summary>
    /// Turns on the monitor whose device path contains <paramref name="monitorId"/> (extends the desktop),
    /// leaving every other display as it is. The legacy ChangeDisplaySettingsEx cannot activate a detached
    /// display on current Windows versions, so this goes through SetDisplayConfig.
    /// </summary>
    public static bool TryActivateMonitor(string monitorId)
    {
        if (GetDisplayConfigBufferSizes(QDC_ALL_PATHS, out var pathCount, out var modeCount) != 0)
            return false;
        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
        if (QueryDisplayConfig(QDC_ALL_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
            return false;
        Array.Resize(ref paths, (int)pathCount);
        Array.Resize(ref modes, (int)modeCount);

        var active = paths.Where(p => (p.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0).ToList();
        bool SourceInUse(DISPLAYCONFIG_PATH_INFO p) => active.Any(a =>
            a.sourceInfo.adapterId.LowPart == p.sourceInfo.adapterId.LowPart &&
            a.sourceInfo.adapterId.HighPart == p.sourceInfo.adapterId.HighPart &&
            a.sourceInfo.id == p.sourceInfo.id);

        foreach (var path in paths)
        {
            if (path.targetInfo.targetAvailable == 0 || !TargetMatches(path, monitorId))
                continue;
            if ((path.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0)
                return true;
            if (SourceInUse(path))
                continue;

            var candidate = path;
            candidate.flags |= DISPLAYCONFIG_PATH_ACTIVE;
            candidate.sourceInfo.modeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
            candidate.targetInfo.modeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
            var newPaths = active.Append(candidate).ToArray();
            var r = SetDisplayConfig((uint)newPaths.Length, newPaths, (uint)modes.Length, modes,
                SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE);
            if (r == 0)
                return true;
            Log.Warn($"SetDisplayConfig (activate {monitorId}) failed: {r}");
        }

        // Diagnostics: what targets did Windows offer?
        foreach (var p in paths.Where(p => p.targetInfo.targetAvailable != 0).DistinctBy(p => (p.targetInfo.adapterId.LowPart, p.targetInfo.id)))
            Log.Info($"  target {p.targetInfo.adapterId.LowPart:X}/{p.targetInfo.id} active={(p.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0} path={TargetPath(p)}");
        return false;
    }

    private static string TargetPath(DISPLAYCONFIG_PATH_INFO path)
    {
        var name = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
        name.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
        name.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
        name.header.adapterId = path.targetInfo.adapterId;
        name.header.id = path.targetInfo.id;
        var r = DisplayConfigGetDeviceInfo(ref name);
        return r == 0 ? $"{name.monitorFriendlyDeviceName} {name.monitorDevicePath}" : $"error {r}";
    }

    private static bool TargetMatches(DISPLAYCONFIG_PATH_INFO path, string monitorId)
    {
        var name = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
        name.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
        name.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
        name.header.adapterId = path.targetInfo.adapterId;
        name.header.id = path.targetInfo.id;
        return DisplayConfigGetDeviceInfo(ref name) == 0 &&
               name.monitorDevicePath?.Contains(monitorId, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool TryFindSource(string gdiName, out LUID adapter, out uint sourceId)
    {
        adapter = default;
        sourceId = 0;
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != 0)
            return false;
        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
            return false;

        for (var i = 0; i < pathCount; i++)
        {
            var name = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
            name.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
            name.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
            name.header.adapterId = paths[i].sourceInfo.adapterId;
            name.header.id = paths[i].sourceInfo.id;
            if (DisplayConfigGetDeviceInfo(ref name) == 0 &&
                string.Equals(name.viewGdiDeviceName, gdiName, StringComparison.OrdinalIgnoreCase))
            {
                adapter = paths[i].sourceInfo.adapterId;
                sourceId = paths[i].sourceInfo.id;
                return true;
            }
        }
        return false;
    }

    /// <summary>Sets the DPI scale (100, 125, 150 …) of an active display. Best effort.</summary>
    public static bool TrySetScale(string gdiName, int percent)
    {
        try
        {
            if (!TryFindSource(gdiName, out var adapter, out var sourceId))
                return false;

            var get = new DPI_SCALE_GET();
            get.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_DPI_SCALE;
            get.header.size = (uint)Marshal.SizeOf<DPI_SCALE_GET>();
            get.header.adapterId = adapter;
            get.header.id = sourceId;
            if (DisplayConfigGetDeviceInfo(ref get) != 0)
                return false;

            // Values are relative to the recommended scale (index offset in DpiSteps).
            var recommendedIndex = Math.Abs(get.minScaleRel);
            var wantedIndex = Array.IndexOf(DpiSteps, ClosestStep(percent));
            var rel = Math.Clamp(wantedIndex - recommendedIndex, get.minScaleRel, get.maxScaleRel);
            if (rel == get.curScaleRel)
                return true;

            var set = new DPI_SCALE_SET();
            set.header.type = DISPLAYCONFIG_DEVICE_INFO_SET_DPI_SCALE;
            set.header.size = (uint)Marshal.SizeOf<DPI_SCALE_SET>();
            set.header.adapterId = adapter;
            set.header.id = sourceId;
            set.scaleRel = rel;
            return DisplayConfigSetDeviceInfo(ref set) == 0;
        }
        catch
        {
            return false;
        }
    }

    public static int ClosestStep(int percent) =>
        DpiSteps.OrderBy(s => Math.Abs(s - percent)).First();
}
