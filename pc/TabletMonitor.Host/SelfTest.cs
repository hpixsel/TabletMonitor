using System.IO;
using TabletMonitor.Display;
using TabletMonitor.Streaming;

namespace TabletMonitor;

/// <summary>
/// "TabletMonitor.exe --selftest out.h264 [W H Hz seconds]" – attaches the virtual display, records the
/// encoded stream to a file and detaches again. Used to verify driver + capture + encoder without a tablet.
/// </summary>
internal static class SelfTest
{
    public static int Run(string[] args)
    {
        var output = args.ElementAtOrDefault(1) ?? "selftest.h264";
        var width = int.Parse(args.ElementAtOrDefault(2) ?? "1920");
        var height = int.Parse(args.ElementAtOrDefault(3) ?? "1080");
        var hz = int.Parse(args.ElementAtOrDefault(4) ?? "60");
        var seconds = int.Parse(args.ElementAtOrDefault(5) ?? "3");
        var report = new List<string>();

        try
        {
            report.Add($"driver installed: {VirtualDisplayDriver.IsInstalled}");
            var gdi = Setup.ServiceClient.EnsureVirtualDisplayAsync().GetAwaiter().GetResult() ?? throw new InvalidOperationException("virtual display not found");
            report.Add($"virtual display: {gdi}");
            report.Add($"attach: {DisplayManager.Attach(gdi, new DisplayMode(width, height, hz, false), null)}");
            Thread.Sleep(1500);
            report.Add($"bounds: {DisplayManager.GetBounds(gdi)}");

            using var file = File.Create(output);
            int frames = 0, keyFrames = 0, cursorShapes = 0;
            long bytes = 0;
            string encoder = "?";
            using (var streamer = new ScreenStreamer(gdi, new StreamSettings(hz, 20_000_000, 1.0)))
            {
                streamer.EncoderChanged += e => encoder = e;
                streamer.FrameEncoded += f =>
                {
                    lock (file)
                    {
                        file.Write(f.Data);
                        frames++;
                        bytes += f.Data.Length;
                        if (f.KeyFrame) keyFrames++;
                    }
                };
                streamer.CursorShapeChanged += _ => cursorShapes++;
                streamer.Error += ex => report.Add($"streamer error: {ex.Message}");

                // Ask for a keyframe every second, like a tablet whose decoder restarts.
                using var keyTimer = new Timer(_ => streamer.RequestKeyFrame(), null, 1000, 1000);

                // Animate a window on the virtual display so frames are produced.
                var bounds = DisplayManager.GetBounds(gdi)!.Value;
                var ui = new Thread(() => AnimateWindow(bounds, seconds));
                ui.SetApartmentState(ApartmentState.STA);
                ui.Start();
                ui.Join();
            }
            report.Add($"encoder: {encoder}");
            report.Add($"frames: {frames}, keyframes: {keyFrames}, bytes: {bytes}, cursor shapes: {cursorShapes}");
            return frames > 0 ? 0 : 3;
        }
        catch (Exception ex)
        {
            report.Add($"FAILED: {ex}");
            return 1;
        }
        finally
        {
            if (DisplayManager.FindVirtualDisplay() is { } gdi)
                report.Add($"detach: {DisplayManager.Detach(gdi)}");
            Setup.ServiceClient.SendAsync("DISABLE").GetAwaiter().GetResult();
            File.WriteAllLines(output + ".txt", report);
        }
    }

    private static void AnimateWindow(System.Drawing.Rectangle bounds, int seconds)
    {
        var form = new System.Windows.Forms.Form
        {
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
            StartPosition = System.Windows.Forms.FormStartPosition.Manual,
            Bounds = bounds,
            TopMost = true,
            ShowInTaskbar = false,
            BackColor = System.Drawing.Color.FromArgb(30, 30, 40),
        };
        var start = DateTime.UtcNow;
        var timer = new System.Windows.Forms.Timer { Interval = 16 };
        form.Paint += (_, e) =>
        {
            var t = (DateTime.UtcNow - start).TotalSeconds;
            var x = (int)((Math.Sin(t * 2) * 0.4 + 0.5) * (bounds.Width - 200));
            using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0x4F, 0x6B, 0xFF));
            e.Graphics.FillEllipse(brush, x, bounds.Height / 2 - 100, 200, 200);
            using var font = new System.Drawing.Font("Segoe UI", 48);
            e.Graphics.DrawString($"TabletMonitor self-test {t:0.00}s", font, System.Drawing.Brushes.White, 60, 60);
        };
        timer.Tick += (_, _) =>
        {
            if ((DateTime.UtcNow - start).TotalSeconds > seconds)
                form.Close();
            form.Invalidate();
        };
        timer.Start();
        System.Windows.Forms.Application.Run(form);
    }
}
