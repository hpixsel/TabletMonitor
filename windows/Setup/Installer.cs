using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Win32;
using TabletMonitor.Display;

namespace TabletMonitor.Setup;

/// <summary>
/// The exe installs itself: one UAC prompt copies it to Program Files, installs the bundled
/// virtual display driver, opens the firewall for discovery and registers an uninstaller.
/// </summary>
public static class Installer
{
    private const string AppName = "TabletMonitor";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\TabletMonitor";
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    public static readonly string InstallDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);
    public static readonly string InstalledExe = Path.Combine(InstallDirectory, AppName + ".exe");
    private static readonly string DataDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppName);

    public static string CurrentExe => Environment.ProcessPath!;

    public static bool IsRunningInstalled =>
        string.Equals(Path.GetFullPath(CurrentExe), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    public static bool IsInstalled => File.Exists(InstalledExe) && VirtualDisplayDriver.IsInstalled;

    /// <summary>Published single-file build (development builds only install the driver).</summary>
#pragma warning disable IL3000 // an empty Location is exactly how a single-file build is detected
    public static bool IsSingleFile => string.IsNullOrEmpty(typeof(Installer).Assembly.Location);
#pragma warning restore IL3000

    /// <summary>This exe runs from elsewhere while Program Files holds a different build.</summary>
    public static bool IsNewerThanInstalled => IsSingleFile && !IsRunningInstalled && File.Exists(InstalledExe) && !SameFile(CurrentExe, InstalledExe);

    /// <summary>This exe is a copy of the installed one started from elsewhere (e.g. Downloads).</summary>
    public static bool IsCopyOfInstalled => IsSingleFile && !IsRunningInstalled && File.Exists(InstalledExe) && SameFile(CurrentExe, InstalledExe);

    private static bool SameFile(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        return fa.Length == fb.Length && fa.LastWriteTimeUtc == fb.LastWriteTimeUtc;
    }

    // ---------------- Called from the normal (non-elevated) app ----------------

    /// <summary>Runs this exe elevated with the given arguments and waits. Returns false when the user declined UAC.</summary>
    public static Task<bool> RunElevatedAsync(params string[] args) => RunElevatedAsync(CurrentExe, args);

    /// <summary>Runs <paramref name="exe"/> elevated (e.g. a downloaded update installing itself).</summary>
    public static async Task<bool> RunElevatedAsync(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        psi.ArgumentList.Add("--elevated");
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync();
            if (p.ExitCode != 0)
                throw new InvalidOperationException(Localization.Strings.OperationFailed(p.ExitCode));
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false; // ERROR_CANCELLED – UAC declined
        }
    }

    public static Task<bool> InstallAsync() => RunElevatedAsync("install");

    /// <summary>Installs a downloaded update (it copies itself to Program Files), then restarts into it.</summary>
    public static async Task<bool> InstallUpdateAsync(string downloadedExe)
    {
        if (!await RunElevatedAsync(downloadedExe, "install"))
            return false;
        LaunchInstalledAndExit();
        return true;
    }

    /// <summary>Removes the previous exe that an update renamed out of the way.</summary>
    public static void CleanUpAfterUpdate()
    {
        try { File.Delete(InstalledExe + ".old"); }
        catch { /* still in use or not ours to delete – next time */ }
    }
    public static Task<bool> AddResolutionAsync(int width, int height) => RunElevatedAsync("add-mode", $"{width}x{height}");
    public static Task<bool> UninstallAsync() => RunElevatedAsync("uninstall");

    public static void LaunchInstalledAndExit()
    {
        var psi = new ProcessStartInfo(InstalledExe) { UseShellExecute = true, WorkingDirectory = InstallDirectory };
        psi.ArgumentList.Add("--wait-for-previous");
        Process.Start(psi);
        System.Windows.Application.Current.Shutdown(); // disposes the tray icon and releases the single-instance mutex
    }

    /// <summary>Starts a new instance of this exe and exits (e.g. after switching the language).</summary>
    public static void Restart()
    {
        var psi = new ProcessStartInfo(CurrentExe) { UseShellExecute = true };
        psi.ArgumentList.Add("--wait-for-previous");
        Process.Start(psi);
        System.Windows.Application.Current.Shutdown();
    }

    public static bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(AppName) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value)
                key.SetValue(AppName, $"\"{(File.Exists(InstalledExe) ? InstalledExe : CurrentExe)}\" --minimized");
            else
                key.DeleteValue(AppName, throwOnMissingValue: false);
        }
    }

    // ---------------- Elevated entry point ----------------

    /// <summary>Handles "--elevated &lt;op&gt;". Returns the process exit code.</summary>
    public static int RunElevatedOperation(string[] args)
    {
        try
        {
            switch (args.ElementAtOrDefault(1))
            {
                case "install":
                    InstallElevated();
                    return 0;
                case "install-driver":
                    var driverDir = Path.Combine(DataDirectory, "driver");
                    ExtractResources("driver/", driverDir);
                    VirtualDisplayDriver.Install(driverDir);
                    return 0;
                case "add-mode":
                    var parts = args[2].Split('x');
                    VirtualDisplayDriver.WriteSettings([(int.Parse(parts[0]), int.Parse(parts[1]))]);
                    VirtualDisplayDriver.Restart();
                    return 0;
                case "uninstall":
                    UninstallElevated();
                    return 0;
                default:
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Elevated operation '{string.Join(' ', args)}' failed", ex);
            return 1;
        }
    }

    private static void InstallElevated()
    {
        Log.Info("Installing TabletMonitor");

        // 1. Application files (the running service locks the exe – stop it first)
        Directory.CreateDirectory(InstallDirectory);
        StopService();
        if (!IsRunningInstalled)
            ReplaceFile(CurrentExe, InstalledExe);

        // 2. Driver
        var driverDir = Path.Combine(DataDirectory, "driver");
        ExtractResources("driver/", driverDir);
        VirtualDisplayDriver.Install(driverDir);

        // 3. Background service for admin-only actions (adapter on/off, new resolutions) – no UAC in daily use.
        RunHidden("sc.exe", $"create {DisplayService.Name} binPath= \"\\\"{InstalledExe}\\\" --service\" start= auto obj= LocalSystem DisplayName= \"TabletMonitor\"");
        RunHidden("sc.exe", $"config {DisplayService.Name} binPath= \"\\\"{InstalledExe}\\\" --service\" start= auto");
        RunHidden("sc.exe", $"description {DisplayService.Name} \"Turns the TabletMonitor virtual display on and off.\"");
        RunHidden("sc.exe", $"failure {DisplayService.Name} reset= 86400 actions= restart/5000/restart/5000/restart/5000");
        RunHidden("sc.exe", $"start {DisplayService.Name}");

        // Until a tablet connects the virtual adapter stays off, so no phantom screen shows up in Windows.
        VirtualDisplayDriver.SetEnabled(false);

        // 4. Firewall – tablets announce themselves via UDP broadcast.
        RunHidden("netsh", $"advfirewall firewall delete rule name=\"{AppName}\"");
        RunHidden("netsh", $"advfirewall firewall add rule name=\"{AppName}\" dir=in action=allow program=\"{InstalledExe}\" enable=yes profile=private,domain,public");

        // 5. Start menu shortcut
        CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppName + ".lnk"), InstalledExe);

        // 6. "Apps & features" entry
        using (var key = Registry.LocalMachine.CreateSubKey(UninstallKey))
        {
            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0");
            key.SetValue("Publisher", AppName);
            key.SetValue("DisplayIcon", InstalledExe);
            key.SetValue("InstallLocation", InstallDirectory);
            key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
            key.SetValue("NoModify", 1);
            key.SetValue("NoRepair", 1);
            key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024));
        }
        Log.Info("Installation finished");
    }

    private static void UninstallElevated()
    {
        Log.Info("Uninstalling TabletMonitor");
        StopService();
        RunHidden("sc.exe", $"delete {DisplayService.Name}");
        VirtualDisplayDriver.Uninstall();
        RunHidden("netsh", $"advfirewall firewall delete rule name=\"{AppName}\"");
        TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppName + ".lnk"));
        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        try { Directory.Delete(DataDirectory, recursive: true); } catch { /* ignore */ }
        try { Directory.Delete(VirtualDisplayDriver.ConfigDirectory, recursive: true); } catch { /* ignore */ }

        // The running exe cannot delete itself – remove the folder after we exit.
        RunHidden("cmd.exe", $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{InstallDirectory}\"", wait: false);
    }

    /// <summary>
    /// Copies <paramref name="source"/> over <paramref name="target"/>. A running exe cannot be overwritten but can be
    /// renamed, so an exe still in use (the app being updated) is moved aside first.
    /// </summary>
    private static void ReplaceFile(string source, string target)
    {
        try
        {
            File.Copy(source, target, overwrite: true);
        }
        catch (IOException)
        {
            var old = target + ".old";
            File.Delete(old);
            File.Move(target, old);
            File.Copy(source, target);
        }
    }

    private static void StopService()
    {
        try
        {
            using var sc = new System.ServiceProcess.ServiceController(DisplayService.Name);
            if (sc.Status != System.ServiceProcess.ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
            }
        }
        catch (InvalidOperationException)
        {
            // Not installed yet.
        }
    }

    /// <summary>Extracts embedded resources with the given prefix (e.g. "adb/") into a directory.</summary>
    public static void ExtractResources(string prefix, string directory)
    {
        Directory.CreateDirectory(directory);
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var target = Path.Combine(directory, name[prefix.Length..]);
            using var src = asm.GetManifestResourceStream(name)!;
            if (File.Exists(target) && new FileInfo(target).Length == src.Length)
                continue;
            using var dst = File.Create(target);
            src.CopyTo(dst);
        }
    }

    public static bool HasResource(string name) =>
        Assembly.GetExecutingAssembly().GetManifestResourceInfo(name) != null;

    private static void CreateShortcut(string path, string target)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = target;
            link.WorkingDirectory = Path.GetDirectoryName(target);
            link.Description = "Tablet as an extra display";
            link.Save();
        }
        catch (Exception ex)
        {
            Log.Warn($"Shortcut creation failed: {ex.Message}");
        }
    }

    private static void RunHidden(string file, string args, bool wait = true)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { CreateNoWindow = true, UseShellExecute = false });
        if (wait)
            p?.WaitForExit();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* ignore */ }
    }
}
