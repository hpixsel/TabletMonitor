using System.Drawing;
using System.Runtime.InteropServices;

namespace TabletMonitor.Streaming;

public sealed record CursorShape(int Width, int Height, int HotX, int HotY, uint[] Argb);

/// <summary>
/// Follows the system cursor. The tablet draws it as an overlay, so mouse movement does not
/// require encoding new video frames.
/// </summary>
public sealed unsafe class CursorTracker
{
    private IntPtr _lastHandle;
    private int _lastX = int.MinValue, _lastY = int.MinValue;
    private bool _lastVisible;

    public void Reset()
    {
        _lastHandle = IntPtr.Zero;
        _lastX = _lastY = int.MinValue;
        _lastVisible = false;
    }

    /// <summary>
    /// Checks the cursor against the display <paramref name="bounds"/> (desktop pixels) and reports changes.
    /// Positions are converted to video-frame pixels.
    /// </summary>
    public void Poll(Rectangle bounds, int frameWidth, int frameHeight, Action<CursorShape> onShape, Action<int, int, bool> onPosition)
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref info))
            return;

        var visible = (info.flags & CURSOR_SHOWING) != 0 && info.hCursor != IntPtr.Zero && bounds.Contains(info.ptScreenPos.X, info.ptScreenPos.Y);
        var scaleX = (double)frameWidth / bounds.Width;
        var scaleY = (double)frameHeight / bounds.Height;
        var x = (int)((info.ptScreenPos.X - bounds.X) * scaleX);
        var y = (int)((info.ptScreenPos.Y - bounds.Y) * scaleY);

        if (visible && info.hCursor != _lastHandle)
        {
            var dpi = GetDpi(info.ptScreenPos);
            if (Render(info.hCursor, dpi * scaleX) is { } shape)
            {
                _lastHandle = info.hCursor;
                onShape(shape);
            }
        }

        if (visible != _lastVisible || (visible && (x != _lastX || y != _lastY)))
        {
            _lastVisible = visible;
            _lastX = x;
            _lastY = y;
            onPosition(x, y, visible);
        }
    }

    private static double GetDpi(POINT pt)
    {
        var monitor = MonitorFromPoint(pt, 2);
        return GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 ? dpiX : 96;
    }

    /// <summary>Renders a cursor to ARGB by drawing it on black and on white and deriving alpha from the difference.</summary>
    private static CursorShape? Render(IntPtr cursor, double dpi)
    {
        if (!GetIconInfo(cursor, out var ii))
            return null;
        try
        {
            int baseW, baseH;
            if (ii.hbmColor != IntPtr.Zero)
            {
                GetObject(ii.hbmColor, Marshal.SizeOf<BITMAP>(), out var bmp);
                (baseW, baseH) = (bmp.bmWidth, bmp.bmHeight);
            }
            else
            {
                GetObject(ii.hbmMask, Marshal.SizeOf<BITMAP>(), out var bmp);
                (baseW, baseH) = (bmp.bmWidth, bmp.bmHeight / 2);
            }

            var target = (int)Math.Round(32 * dpi / 96);
            var scale = baseW >= target ? 1.0 : (double)target / baseW;
            var w = Math.Clamp((int)Math.Round(baseW * scale), 1, 256);
            var h = Math.Clamp((int)Math.Round(baseH * scale), 1, 256);
            var hotX = (int)Math.Round(ii.xHotspot * scale);
            var hotY = (int)Math.Round(ii.yHotspot * scale);

            var onBlack = Draw(cursor, w, h, 0x00);
            var onWhite = Draw(cursor, w, h, 0xFF);
            var argb = new uint[w * h];
            for (var i = 0; i < argb.Length; i++)
            {
                var b = onBlack[i];
                var wv = onWhite[i];
                int bG = (int)((b >> 8) & 0xFF), wG = (int)((wv >> 8) & 0xFF);
                if (wG < bG)
                {
                    argb[i] = 0xFF000000; // XOR (inverting) pixel – draw it black
                    continue;
                }
                var alpha = 255 - (wG - bG);
                if (alpha <= 0)
                    continue;
                uint r = (uint)Math.Min(255, ((b >> 16) & 0xFF) * 255 / alpha);
                uint g = (uint)Math.Min(255, bG * 255 / alpha);
                uint bl = (uint)Math.Min(255, (b & 0xFF) * 255 / alpha);
                argb[i] = ((uint)alpha << 24) | (r << 16) | (g << 8) | bl;
            }
            return new CursorShape(w, h, hotX, hotY, argb);
        }
        finally
        {
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
        }
    }

    private static uint[] Draw(IntPtr cursor, int w, int h, byte background)
    {
        var bmi = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h,
            biPlanes = 1,
            biBitCount = 32,
        };
        var dc = CreateCompatibleDC(IntPtr.Zero);
        var dib = CreateDIBSection(dc, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        var old = SelectObject(dc, dib);
        try
        {
            new Span<byte>((void*)bits, w * h * 4).Fill(background);
            DrawIconEx(dc, 0, 0, cursor, w, h, 0, IntPtr.Zero, 3 /* DI_NORMAL */);
            GdiFlush();
            return new ReadOnlySpan<uint>((void*)bits, w * h).ToArray();
        }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(dib);
            DeleteDC(dc);
        }
    }

    private const int CURSOR_SHOWING = 1;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO { public bool fIcon; public int xHotspot; public int yHotspot; public IntPtr hbmMask; public IntPtr hbmColor; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int cx, int cy, int step, IntPtr brush, int flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, int flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int c, out BITMAP bmp);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
}
