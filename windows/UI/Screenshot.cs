using System.Runtime.InteropServices;
using System.Windows;

namespace TabletMonitor.UI;

/// <summary>
/// "TabletMonitor.exe --screenshot out.png connected|devices|empty dark|light en|pl [height]" – renders the main
/// window with sample devices and saves a capture of it (README images). Starts no services.
/// </summary>
internal static class Screenshot
{
    public static async Task RunAsync(string[] args)
    {
        var output = args.ElementAtOrDefault(1) ?? "screenshot.png";
        var state = args.ElementAtOrDefault(2) ?? "connected";
        var theme = args.ElementAtOrDefault(3) ?? "dark";
        Localization.Strings.Initialize(args.ElementAtOrDefault(4) ?? Localization.Strings.English);
        var height = double.Parse(args.ElementAtOrDefault(5) ?? "720", System.Globalization.CultureInfo.InvariantCulture);

#pragma warning disable WPF0001 // ThemeMode is marked experimental
        Application.Current.ThemeMode = theme == "light" ? ThemeMode.Light : ThemeMode.Dark;
#pragma warning restore WPF0001

        var viewModel = new MainViewModel();
        if (state != "empty")
            viewModel.LoadDemo(state == "connected");

        var window = new MainWindow(viewModel)
        {
            AllowClose = true,
            Topmost = true,
            Height = height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        window.Show();
        window.Activate();
        await Task.Delay(1500); // layout, theme backdrop and fonts

        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out var r, Marshal.SizeOf<RECT>());
        using (var bmp = new System.Drawing.Bitmap(r.Right - r.Left, r.Bottom - r.Top))
        {
            using (var g = System.Drawing.Graphics.FromImage(bmp))
                g.CopyFromScreen(r.Left, r.Top, 0, 0, bmp.Size);
            bmp.Save(output, System.Drawing.Imaging.ImageFormat.Png);
        }
        window.Close();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
}
