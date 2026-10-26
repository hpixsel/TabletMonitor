using System.ComponentModel;
using System.Windows;
using TabletMonitor.Localization;
using Forms = System.Windows.Forms;

namespace TabletMonitor.UI;

/// <summary>Owns the main window and the tray icon; the app lives in the tray when the window is closed.</summary>
public sealed class AppController : IDisposable
{
    private readonly bool _startMinimized;
    private readonly MainViewModel _viewModel = new();
    private readonly Forms.NotifyIcon _tray;
    private readonly Forms.ToolStripMenuItem _statusItem;
    private readonly Forms.ToolStripMenuItem _disconnectItem;
    private MainWindow? _window;

    public AppController(bool startMinimized)
    {
        _startMinimized = startMinimized;

        _statusItem = new Forms.ToolStripMenuItem { Enabled = false };
        _disconnectItem = new Forms.ToolStripMenuItem(Strings.TrayTurnOff, null, (_, _) => _viewModel.DisconnectCommand.Execute(null));
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_disconnectItem);
        menu.Items.Add(Strings.TrayOpen, null, (_, _) => ShowMainWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Strings.TrayExit, null, (_, _) => Exit());

        _tray = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "TabletMonitor",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                ShowMainWindow();
        };
        _tray.BalloonTipClicked += (_, _) => ShowMainWindow();

        _viewModel.PropertyChanged += OnViewModelChanged;
        _viewModel.Notify += (title, text) => Application.Current.Dispatcher.BeginInvoke(() => ShowNotification(title, text));
        UpdateTray();

        Application.Current.SessionEnding += (_, _) => Dispose();
    }

    public Task StartAsync()
    {
        _viewModel.Start();
        if (!_startMinimized || _viewModel.NeedsSetup)
            ShowMainWindow();
        return Task.CompletedTask;
    }

    public void ShowMainWindow()
    {
        if (_window == null)
        {
            _window = new MainWindow(_viewModel);
            _window.HiddenToTray += OnHiddenToTray;
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void OnHiddenToTray()
    {
        if (!_viewModel.TrayHintShown)
        {
            ShowNotification(Strings.TrayHintTitle, Strings.TrayHintText, force: true);
            _viewModel.TrayHintShown = true;
        }
    }

    private void ShowNotification(string title, string text, bool force = false)
    {
        // Inside the open window the state is already visible.
        if (!force && _window is { IsVisible: true, IsActive: true })
            return;
        _tray.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.None);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.StatusText) or nameof(MainViewModel.IsConnected))
            UpdateTray();
    }

    private void UpdateTray()
    {
        _statusItem.Text = _viewModel.StatusText;
        _disconnectItem.Visible = _viewModel.IsConnected;
        var text = "TabletMonitor – " + _viewModel.StatusText;
        _tray.Text = text.Length > 63 ? text[..63] : text;
    }

    private void Exit()
    {
        Dispose();
        Application.Current.Shutdown();
    }

    private static System.Drawing.Icon LoadIcon()
    {
        var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
        return new System.Drawing.Icon(info!.Stream, Forms.SystemInformation.SmallIconSize);
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _viewModel.Dispose(); // ends the session → the virtual screen is removed
        _tray.Visible = false;
        _tray.Dispose();
        if (_window != null)
        {
            _window.AllowClose = true;
            _window.Close();
        }
    }
}
