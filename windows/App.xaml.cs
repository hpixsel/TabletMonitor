using System.Windows;
using TabletMonitor.Setup;
using TabletMonitor.UI;

namespace TabletMonitor;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private AppController? _controller;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        Localization.Strings.Initialize(Core.AppSettings.Load().Language);

        switch (args.FirstOrDefault())
        {
            case "--service":
                // Runs under the Service Control Manager; Run blocks until the service stops.
                System.ServiceProcess.ServiceBase.Run(new DisplayService());
                Shutdown();
                return;
            case "--elevated":
                Shutdown(Installer.RunElevatedOperation(args));
                return;
            case "--selftest":
                Shutdown(SelfTest.Run(args));
                return;
            case "--screenshot":
                await UI.Screenshot.RunAsync(args);
                Shutdown();
                return;
            case "--touchtest":
                Shutdown(TouchTest.Run(args));
                return;
            case "--driver-settings":
                // Rewrites the driver configuration (takes effect after the device restarts).
                Display.VirtualDisplayDriver.WriteSettings([], logging: args.Contains("--logging"));
                Shutdown();
                return;
            case "--uninstall":
                await UninstallAsync();
                return;
        }

        // Started again from Downloads etc.: run the installed copy (its firewall rule is bound to that path).
        if (Installer.IsCopyOfInstalled)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Installer.InstalledExe) { UseShellExecute = true });
            Shutdown();
            return;
        }

        _singleInstance = new Mutex(true, @"Local\TabletMonitor.SingleInstance", out var first);
        // After installing or switching the language the previous instance is still exiting – wait for it.
        if (!first && args.Contains("--wait-for-previous"))
        {
            try { first = _singleInstance.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { first = true; }
        }
        if (!first)
        {
            SingleInstance.SignalExisting();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("Unhandled UI exception", ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("Unhandled exception", ex.ExceptionObject as Exception);

        _controller = new AppController(startMinimized: args.Contains("--minimized"));
        SingleInstance.Listen(() => Dispatcher.Invoke(() => _controller.ShowMainWindow()));
        await _controller.StartAsync();
    }

    private async Task UninstallAsync()
    {
        var answer = MessageBox.Show(
            Localization.Strings.UninstallConfirm,
            "TabletMonitor", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
        {
            try
            {
                if (await Installer.UninstallAsync())
                    MessageBox.Show(Localization.Strings.Uninstalled, "TabletMonitor", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "TabletMonitor", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
