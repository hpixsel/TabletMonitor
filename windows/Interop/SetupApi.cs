using System.Runtime.InteropServices;

namespace TabletMonitor.Interop;

internal static class SetupApi
{
    public static readonly Guid GUID_DEVCLASS_DISPLAY = new("4d36e968-e325-11ce-bfc1-08002be10318");

    public const uint DIGCF_PRESENT = 0x2;
    public const uint DICD_GENERATE_ID = 0x1;
    public const uint DIF_REGISTERDEVICE = 0x19;
    public const uint DIF_REMOVE = 0x5;
    public const uint SPDRP_HARDWAREID = 0x1;
    public const uint INSTALLFLAG_FORCE = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;

        public static SP_DEVINFO_DATA Create() => new() { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetupDiCreateDeviceInfo(IntPtr deviceInfoSet, string deviceName, ref Guid classGuid,
        string? deviceDescription, IntPtr hwndParent, uint creationFlags, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetupDiSetDeviceRegistryProperty(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        uint property, byte[] propertyBuffer, uint propertyBufferSize);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetupDiGetDeviceRegistryProperty(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        uint property, out uint propertyRegDataType, byte[]? propertyBuffer, uint propertyBufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetupDiGetDeviceInstanceId(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        char[] deviceInstanceId, int deviceInstanceIdSize, out int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiCallClassInstaller(uint installFunction, IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr hwndParent, string hardwareId, string fullInfPath,
        uint installFlags, out bool bRebootRequired);

    /// <summary>Returns instance ids of present display devices with the given hardware id.</summary>
    public static List<string> FindDevices(string hardwareId)
    {
        var result = new List<string>();
        var guid = GUID_DEVCLASS_DISPLAY;
        var set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
        if (set == new IntPtr(-1))
            return result;
        try
        {
            var data = SP_DEVINFO_DATA.Create();
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                SetupDiGetDeviceRegistryProperty(set, ref data, SPDRP_HARDWAREID, out _, null, 0, out var size);
                if (size == 0)
                    continue;
                var buffer = new byte[size];
                if (!SetupDiGetDeviceRegistryProperty(set, ref data, SPDRP_HARDWAREID, out _, buffer, size, out _))
                    continue;
                var ids = System.Text.Encoding.Unicode.GetString(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                if (!ids.Any(id => string.Equals(id, hardwareId, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var chars = new char[512];
                if (SetupDiGetDeviceInstanceId(set, ref data, chars, chars.Length, out var len))
                    result.Add(new string(chars, 0, Math.Max(0, len - 1)));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return result;
    }

    /// <summary>Creates a root-enumerated device node with the given hardware id (like "devcon install").</summary>
    public static void CreateRootDevice(string hardwareId)
    {
        var guid = GUID_DEVCLASS_DISPLAY;
        var set = SetupDiCreateDeviceInfoList(ref guid, IntPtr.Zero);
        if (set == new IntPtr(-1))
            throw new System.ComponentModel.Win32Exception();
        try
        {
            var data = SP_DEVINFO_DATA.Create();
            if (!SetupDiCreateDeviceInfo(set, "Display", ref guid, null, IntPtr.Zero, DICD_GENERATE_ID, ref data))
                throw new System.ComponentModel.Win32Exception();
            var ids = System.Text.Encoding.Unicode.GetBytes(hardwareId + "\0\0");
            if (!SetupDiSetDeviceRegistryProperty(set, ref data, SPDRP_HARDWAREID, ids, (uint)ids.Length))
                throw new System.ComponentModel.Win32Exception();
            if (!SetupDiCallClassInstaller(DIF_REGISTERDEVICE, set, ref data))
                throw new System.ComponentModel.Win32Exception();
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }
}
