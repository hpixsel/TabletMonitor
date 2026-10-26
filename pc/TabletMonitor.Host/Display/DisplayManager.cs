using System.Drawing;
using TabletMonitor.Interop;
using static TabletMonitor.Interop.User32;

namespace TabletMonitor.Display;

public sealed record DisplayMode(int Width, int Height, int RefreshRate, bool Portrait)
{
    public override string ToString() => $"{Width}×{Height} @ {RefreshRate} Hz{(Portrait ? " (portrait)" : "")}";
}

/// <summary>Attaches/detaches the virtual monitor to the Windows desktop. No admin rights required.</summary>
public static class DisplayManager
{
    /// <summary>EDID id of the bundled driver's monitor (appears in its device path).</summary>
    private const string VirtualMonitorId = "MTT1337";

    /// <summary>GDI name (\\.\DISPLAYn) of the virtual display, or null when the driver is missing.</summary>
    public static string? FindVirtualDisplay()
    {
        for (var i = 0; ; i++)
        {
            var dd = DISPLAY_DEVICE.Create();
            if (!EnumDisplayDevices(null, i, ref dd, 0))
                return null;
            if (dd.DeviceID?.Contains("MttVDD", StringComparison.OrdinalIgnoreCase) == true)
                return dd.DeviceName;
        }
    }

    public static bool IsAttached(string gdiName)
    {
        for (var i = 0; ; i++)
        {
            var dd = DISPLAY_DEVICE.Create();
            if (!EnumDisplayDevices(null, i, ref dd, 0))
                return false;
            if (dd.DeviceName == gdiName)
                return (dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0;
        }
    }

    /// <summary>Desktop rectangle of the display in physical pixels.</summary>
    public static Rectangle? GetBounds(string gdiName)
    {
        var dm = DEVMODE.Create();
        if (!EnumDisplaySettingsEx(gdiName, ENUM_CURRENT_SETTINGS, ref dm, 0) || dm.dmPelsWidth == 0)
            return null;
        return new Rectangle(dm.dmPositionX, dm.dmPositionY, dm.dmPelsWidth, dm.dmPelsHeight);
    }

    /// <summary>Bounds of all other attached displays – used to place the tablet next to them.</summary>
    public static List<Rectangle> GetOtherDisplays(string exceptGdiName)
    {
        var list = new List<Rectangle>();
        for (var i = 0; ; i++)
        {
            var dd = DISPLAY_DEVICE.Create();
            if (!EnumDisplayDevices(null, i, ref dd, 0))
                break;
            if (dd.DeviceName == exceptGdiName || (dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0)
                continue;
            if (GetBounds(dd.DeviceName) is { } b)
                list.Add(b);
        }
        return list;
    }

    /// <summary>
    /// Attaches the virtual display with the given mode. When <paramref name="position"/> is null the display
    /// goes to the right of the existing desktop (top-aligned).
    /// </summary>
    public static bool Attach(string gdiName, DisplayMode mode, Point? position)
    {
        if (!IsAttached(gdiName))
        {
            if (!DisplayConfig.TryActivateMonitor(VirtualMonitorId))
            {
                Log.Warn($"Could not activate the virtual monitor ({gdiName})");
                return false;
            }
            for (var i = 0; i < 30 && !IsAttached(gdiName); i++)
                Thread.Sleep(50);
        }

        var others = GetOtherDisplays(gdiName);
        var pos = position ?? DefaultPosition(others);

        var dm = DEVMODE.Create();
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY | DM_POSITION | DM_DISPLAYORIENTATION | DM_BITSPERPEL;
        // Mode width/height are given in the rotated (visible) orientation.
        dm.dmPelsWidth = mode.Portrait ? Math.Min(mode.Width, mode.Height) : Math.Max(mode.Width, mode.Height);
        dm.dmPelsHeight = mode.Portrait ? Math.Max(mode.Width, mode.Height) : Math.Min(mode.Width, mode.Height);
        dm.dmDisplayFrequency = mode.RefreshRate;
        dm.dmDisplayOrientation = mode.Portrait ? DMDO_90 : DMDO_DEFAULT;
        dm.dmBitsPerPel = 32;
        dm.dmPositionX = pos.X;
        dm.dmPositionY = pos.Y;

        var r = ChangeDisplaySettingsEx(gdiName, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
        if (r != DISP_CHANGE_SUCCESSFUL)
        {
            Log.Warn($"Attach {gdiName} {mode} failed: {r}");
            return false;
        }
        r = ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        Log.Info($"Attach {gdiName} {mode} at {pos} -> {r}");
        return r == DISP_CHANGE_SUCCESSFUL;
    }

    /// <summary>Removes the virtual display from the desktop (windows move back to the remaining screens).</summary>
    public static bool Detach(string gdiName)
    {
        var dm = DEVMODE.Create();
        dm.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT;
        var r = ChangeDisplaySettingsEx(gdiName, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
        var r2 = ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        Log.Info($"Detach {gdiName} -> {r}/{r2}");
        return r == DISP_CHANGE_SUCCESSFUL && r2 == DISP_CHANGE_SUCCESSFUL;
    }

    private static Point DefaultPosition(List<Rectangle> others)
    {
        if (others.Count == 0)
            return Point.Empty;
        var right = others.Max(o => o.Right);
        var top = others.Where(o => o.Right == right).Min(o => o.Top);
        return new Point(right, top);
    }
}
