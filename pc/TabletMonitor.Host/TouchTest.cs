using System.IO;
using TabletMonitor.Display;
using TabletMonitor.Input;
using TabletMonitor.Net;

namespace TabletMonitor;

/// <summary>
/// "TabletMonitor.exe --touchtest out.txt" – attaches the virtual display, opens a window on it and injects a tap
/// and a drag through <see cref="TouchInjector"/>; reports what the window received.
/// </summary>
internal static class TouchTest
{
    public static int Run(string[] args)
    {
        var output = args.ElementAtOrDefault(1) ?? "touchtest.txt";
        var report = new List<string>();
        var gdi = Setup.ServiceClient.EnsureVirtualDisplayAsync().GetAwaiter().GetResult();
        var wasAttached = gdi != null && DisplayManager.IsAttached(gdi);
        try
        {
            if (gdi == null)
                throw new InvalidOperationException("virtual display not found");
            if (!wasAttached)
            {
                report.Add($"attach: {DisplayManager.Attach(gdi, new DisplayMode(1920, 1080, 60, false), null)}");
                Thread.Sleep(1000);
            }
            var bounds = DisplayManager.GetBounds(gdi)!.Value;
            report.Add($"bounds: {bounds}");
            report.Add($"sizes: POINTER_INFO {System.Runtime.InteropServices.Marshal.SizeOf<Interop.User32.POINTER_INFO>()} (96), " +
                       $"TOUCH {System.Runtime.InteropServices.Marshal.SizeOf<Interop.User32.POINTER_TOUCH_INFO>()} (144), " +
                       $"TYPE_INFO {System.Runtime.InteropServices.Marshal.SizeOf<Interop.User32.POINTER_TYPE_INFO>()} (152)");

            var events = new List<string>();
            var ui = new Thread(() =>
            {
                var form = new System.Windows.Forms.Form
                {
                    FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
                    StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                    Bounds = bounds,
                    TopMost = true,
                    ShowInTaskbar = false,
                };
                form.MouseDown += (_, e) => events.Add($"MouseDown {e.Location}");
                form.MouseUp += (_, e) => events.Add($"MouseUp {e.Location}");
                form.Click += (_, _) => events.Add("Click");
                form.MouseMove += (_, e) => { if (events.Count < 40) events.Add($"MouseMove {e.Location}"); };
                form.Shown += async (_, _) =>
                {
                    await Task.Delay(500);
                    using var injector = new TouchInjector();
                    PointerData P(PointerState s, float x, float y) => new(0, s, PointerTool.Finger, false, x, y, 1f);

                    // Tap at the centre
                    injector.Inject([P(PointerState.Down, 0.5f, 0.5f)], bounds);
                    await Task.Delay(40);
                    injector.Inject([P(PointerState.Up, 0.5f, 0.5f)], bounds);
                    await Task.Delay(800);
                    events.Add("--- drag");
                    injector.Inject([P(PointerState.Down, 0.3f, 0.3f)], bounds);
                    for (var i = 1; i <= 10; i++)
                    {
                        await Task.Delay(16);
                        injector.Inject([P(PointerState.Move, 0.3f + i * 0.02f, 0.3f)], bounds);
                    }
                    injector.Inject([P(PointerState.Up, 0.5f, 0.3f)], bounds);
                    await Task.Delay(800);

                    events.Add("--- stylus tap");
                    using (var pen = new TouchInjector())
                    {
                        PointerData S(PointerState s) => new(0, s, PointerTool.Stylus, false, 0.6f, 0.6f, 0.5f);
                        pen.Inject([S(PointerState.Hover)], bounds);
                        await Task.Delay(30);
                        pen.Inject([S(PointerState.Down)], bounds);
                        await Task.Delay(40);
                        pen.Inject([S(PointerState.Up)], bounds);
                        await Task.Delay(30);
                        pen.Inject([S(PointerState.HoverExit)], bounds);
                    }
                    await Task.Delay(800);
                    form.Close();
                };
                System.Windows.Forms.Application.Run(form);
            });
            ui.SetApartmentState(ApartmentState.STA);
            ui.Start();
            ui.Join();
            report.AddRange(events);
            return events.Any(e => e.StartsWith("MouseDown")) ? 0 : 3;
        }
        catch (Exception ex)
        {
            report.Add($"FAILED: {ex}");
            return 1;
        }
        finally
        {
            if (gdi != null && !wasAttached)
            {
                report.Add($"detach: {DisplayManager.Detach(gdi)}");
                Setup.ServiceClient.SendAsync("DISABLE").GetAwaiter().GetResult();
            }
            File.WriteAllLines(output, report);
        }
    }
}
