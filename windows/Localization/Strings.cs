using System.Globalization;

namespace TabletMonitor.Localization;

/// <summary>
/// User-facing texts in Polish and English. The language follows Windows (Polish → Polish, anything else →
/// English) unless the user picked one in the app settings. Chosen once at start-up; XAML binds via {x:Static}.
/// Technical messages that only end up in the log stay in English.
/// </summary>
public static class Strings
{
    public const string Auto = "auto";
    public const string Polish = "pl";
    public const string English = "en";

    public static bool IsPolish { get; private set; }

    /// <param name="language"><see cref="Auto"/>, <see cref="Polish"/> or <see cref="English"/>.</param>
    public static void Initialize(string? language)
    {
        IsPolish = language switch
        {
            Polish => true,
            English => false,
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pl",
        };
        var culture = new CultureInfo(IsPolish ? "pl-PL" : "en-US");
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
    }

    private static string T(string pl, string en) => IsPolish ? pl : en;

    // ---------------- Window ----------------

    public static string AppSubtitle => T("Tablet lub telefon jako dodatkowy ekran", "Tablet or phone as an extra display");
    public static string StatusNotConnected => T("Nie połączono", "Not connected");
    public static string StatusConnected(string name) => T($"Połączono z {name}", $"Connected to {name}");
    public static string StatusConnecting(string name) => T($"Łączenie z {name}…", $"Connecting to {name}…");

    // Connection request
    public static string WantsToConnect => T(" chce się połączyć", " wants to connect");
    public static string WantsToConnectHint => T("Po zezwoleniu tablet będzie łączył się automatycznie.", "Once allowed, the tablet will connect automatically.");
    public static string Reject => T("Odrzuć", "Decline");
    public static string Allow => T("Zezwól", "Allow");

    // Setup
    public static string SetupTitleInstall => T("Jeszcze tylko jeden krok", "Just one more step");
    public static string SetupTitleUpdate => T("Dostępna nowa wersja", "New version available");
    public static string SetupTextInstall => T(
        "Żeby Windows mógł dodać tablet jako osobny ekran, TabletMonitor zainstaluje wbudowany sterownik wirtualnego monitora. Zajmie to kilka sekund, a system jednorazowo poprosi o zgodę administratora.",
        "To let Windows add your tablet as a separate display, TabletMonitor installs its built-in virtual monitor driver. It takes a few seconds and Windows will ask for administrator permission once.");
    public static string SetupTextUpdate => T(
        "Ta wersja TabletMonitor różni się od zainstalowanej. Zaktualizuj ją – zajmie to kilka sekund i system poprosi o zgodę administratora.",
        "This version of TabletMonitor differs from the installed one. Update it – it takes a few seconds and Windows will ask for administrator permission.");
    public static string SetupButtonInstall => T("Zainstaluj i uruchom", "Install and start");
    public static string SetupButtonUpdate => T("Zaktualizuj i uruchom", "Update and start");
    public static string SetupNeedsAdmin => T("Instalacja wymaga zgody administratora. Spróbuj ponownie i wybierz „Tak”.", "Installation needs administrator permission. Try again and choose “Yes”.");

    // Active tablet
    public static string WorksAsExtraDisplay => T("Działa jako dodatkowy ekran · ", "Working as an extra display · ");
    public static string TurnOffScreen => T("Wyłącz ekran", "Turn off screen");
    public static string TurnOffScreenTooltip => T("Usuwa ekran tabletu z Windows – okna wracają na pozostałe monitory", "Removes the tablet screen from Windows – windows move back to your other monitors");
    public static string StatResolution => T("rozdzielczość", "resolution");
    public static string StatFps => T("klatek / s", "frames / s");
    public static string StatPing => T("ms ping", "ms ping");
    public static string Encoder => T("Koder: ", "Encoder: ");

    // Display settings
    public static string DisplaySettings => T("Ustawienia ekranu", "Display settings");
    public static string Resolution => T("Rozdzielczość", "Resolution");
    public static string ResolutionHint => T("Niższa = płynniej i mniej danych przez Wi-Fi", "Lower = smoother and less Wi-Fi traffic");
    public static string ResolutionNative(int w, int h) => T($"Natywna – {w}×{h}", $"Native – {w}×{h}");
    public static string ResolutionHalf(int w, int h) => T($"Połowa – {w}×{h} (płynniej)", $"Half – {w}×{h} (smoother)");
    public static string RefreshRate => T("Częstotliwość odświeżania", "Refresh rate");
    public static string RefreshAuto(int hz) => T($"Automatyczne ({hz} Hz)", $"Automatic ({hz} Hz)");
    public static string Refresh30 => T("30 Hz (słabe Wi-Fi)", "30 Hz (weak Wi-Fi)");
    public static string ImageQuality => T("Jakość obrazu", "Image quality");
    public static string QualityLow => T("Oszczędna (słabe Wi-Fi)", "Economy (weak Wi-Fi)");
    public static string QualityBalanced => T("Zrównoważona", "Balanced");
    public static string QualityHigh => T("Wysoka (ostry tekst)", "High (sharp text)");
    public static string Scaling => T("Skalowanie", "Scaling");
    public static string ScalingHint => T("Wielkość tekstu i elementów na tablecie", "Size of text and elements on the tablet");
    public static string ScaleAuto(int percent) => T($"Automatyczne ({percent}%)", $"Automatic ({percent}%)");
    public static string Orientation => T("Orientacja", "Orientation");
    public static string OrientationAuto => T("Jak trzymam tablet", "Follow the tablet");
    public static string OrientationLandscape => T("Pozioma", "Landscape");
    public static string OrientationPortrait => T("Pionowa", "Portrait");
    public static string AutoConnect => T("Łącz automatycznie", "Connect automatically");
    public static string AutoConnectHint => T("Gdy aplikacja na tablecie jest otwarta w tej samej sieci", "When the tablet app is open on the same network");

    // Device list
    public static string AvailableDevices => T("Dostępne urządzenia", "Available devices");
    public static string Refresh => T("Odśwież", "Refresh");
    public static string SearchAgain => T("Szukaj ponownie", "Search again");
    public static string SearchingTablets => T("Szukam tabletów…", "Looking for tablets…");
    public static string Step1 => T("Zainstaluj aplikację TabletMonitor na tablecie lub telefonie z Androidem.", "Install the TabletMonitor app on your Android tablet or phone.");
    public static string SaveApk => T("Zapisz plik instalacyjny (APK)", "Save the installer (APK)");
    public static string Step2 => T(
        "Połącz go z tą samą siecią Wi-Fi co komputer – albo kablem USB (z włączonym debugowaniem USB; wtedy aplikację zainstalujemy za Ciebie).",
        "Connect it to the same Wi-Fi network as this PC – or with a USB cable (with USB debugging on; then we install the app for you).");
    public static string Step3Before => T("Otwórz aplikację i wybierz ", "Open the app and choose ");
    public static string Step3After => T(" – albo kliknij „Połącz” tutaj, gdy tablet pojawi się na liście.", " – or click “Connect” here once the tablet shows up in the list.");

    // Device cards
    public static string ActionConnect => T("Połącz", "Connect");
    public static string ActionConnecting => T("Łączenie…", "Connecting…");
    public static string ActionInstallApp => T("Zainstaluj aplikację", "Install app");
    public static string ActionInstalling => T("Instaluję…", "Installing…");
    public static string ActionConfirmOnTablet => T("Potwierdź na tablecie", "Confirm on the tablet");
    public static string DeviceAppMissing => T("Aplikacja TabletMonitor nie jest zainstalowana na tym urządzeniu", "The TabletMonitor app is not installed on this device");
    public static string DeviceUnauthorized => T("Zezwól na debugowanie USB w okienku na ekranie tabletu", "Allow USB debugging in the prompt on the tablet screen");
    public static string UsbDevice => T("Urządzenie USB", "USB device");

    // App settings
    public static string AppSection => T("Aplikacja", "App");
    public static string StartWithWindows => T("Uruchamiaj razem z Windows", "Start with Windows");
    public static string StartWithWindowsHint => T("Działa w tle w zasobniku – tablet połączy się sam, gdy otworzysz na nim aplikację", "Runs in the tray – the tablet connects by itself when you open the app on it");
    public static string Language => T("Język", "Language");
    public static string LanguageHint => T("Aplikacja uruchomi się ponownie", "The app will restart");
    public static string LanguageAuto => T("Jak w systemie", "Same as Windows");
    public static string Version => T("Wersja ", "Version ");
    public static string EventLog => T("Dziennik zdarzeń", "Event log");

    // Save dialog
    public static string SaveApkTitle => T("Zapisz aplikację na tablet", "Save the tablet app");
    public static string SaveApkFilter => T("Aplikacja Android (*.apk)|*.apk", "Android app (*.apk)|*.apk");

    // ---------------- Notifications ----------------

    public static string ConnectionRequest => T("Prośba o połączenie", "Connection request");
    public static string ConnectionRequestText(string name) => T($"{name} chce używać tego komputera jako ekranu.", $"{name} wants to use this PC as a display.");
    public static string AppInstallFailed => T("Nie udało się zainstalować aplikacji", "Could not install the app");
    public static string AppInstallFailedDetail(string output) => T("Instalacja aplikacji nie powiodła się: ", "App installation failed: ") + output;
    public static string ScreenStartFailed => T("Nie udało się włączyć ekranu", "Could not turn on the display");
    public static string ScreenStartFailedText => T("Wirtualny ekran nie uruchomił się. Szczegóły w dzienniku zdarzeń.", "The virtual display did not start. See the event log for details.");
    public static string ScreenNotEnabled => T("Wirtualny ekran nie został włączony przez system.", "Windows did not turn on the virtual display.");
    public static string ConnectFailed => T("Nie udało się połączyć", "Could not connect");
    public static string TabletDisconnected => T("Tablet rozłączony", "Tablet disconnected");
    public static string NewResolution => T("Nowa rozdzielczość", "New resolution");
    public static string NewResolutionText(int w, int h) => T($"Dodaję {w}×{h} do wirtualnego ekranu – potwierdź prośbę o uprawnienia.", $"Adding {w}×{h} to the virtual display – please approve the permission prompt.");
    public static string SettingsChangeFailed => T("Nie udało się zmienić ustawień", "Could not change the settings");
    public static string ErrorTimeout => T("Tablet nie odpowiada. Upewnij się, że aplikacja jest otwarta.", "The tablet does not respond. Make sure the app is open.");
    public static string ErrorNetwork => T("Brak połączenia z tabletem. Sprawdź, czy oba urządzenia są w tej samej sieci.", "Cannot reach the tablet. Check that both devices are on the same network.");
    public static string StreamError(string message) => T("Błąd przesyłania obrazu: ", "Video streaming error: ") + message;
    public static string OperationFailed(int code) => T($"Operacja zakończyła się błędem ({code}). Szczegóły w pliku log.txt.", $"The operation failed ({code}). See log.txt for details.");

    // Connection end reasons
    public static string ReasonConnectionLost => T("Połączenie przerwane", "Connection lost");
    public static string ReasonTabletEnded => T("Tablet zakończył połączenie", "The tablet ended the connection");
    public static string ReasonTabletNotResponding => T("Tablet przestał odpowiadać", "The tablet stopped responding");
    public static string ReasonDisconnected => T("Rozłączono", "Disconnected");

    // ---------------- Updates ----------------

    public static string UpdateAvailable(string version) => T($"Dostępna jest wersja {version}", $"Version {version} is available");
    public static string UpdateAvailableHint => T("Pobierzemy ją z GitHuba, sprawdzimy sumę kontrolną i zainstalujemy (jedna zgoda administratora).", "It is downloaded from GitHub, verified with its checksum and installed (one administrator prompt).");
    public static string UpdateNow => T("Zaktualizuj", "Update");
    public static string WhatsNew => T("Co nowego", "What's new");
    public static string Downloading(int percent) => T($"Pobieram… {percent}%", $"Downloading… {percent}%");
    public static string InstallingUpdate => T("Instaluję aktualizację…", "Installing the update…");
    public static string UpdateFailed(string message) => T("Aktualizacja nie powiodła się: ", "Update failed: ") + message;
    public static string CheckForUpdates => T("Sprawdzaj aktualizacje", "Check for updates");
    public static string CheckForUpdatesHint => T("Nowe wersje z wydań na GitHubie", "New versions from GitHub releases");

    // ---------------- Tray ----------------

    public static string TrayTurnOff => T("Wyłącz ekran tabletu", "Turn off tablet screen");
    public static string TrayOpen => T("Otwórz TabletMonitor", "Open TabletMonitor");
    public static string TrayExit => T("Zakończ", "Exit");
    public static string TrayHintTitle => T("TabletMonitor działa w tle", "TabletMonitor is running in the background");
    public static string TrayHintText => T("Tablet połączy się automatycznie. Kliknij ikonę w zasobniku, aby otworzyć okno.", "The tablet will connect automatically. Click the tray icon to open the window.");

    // ---------------- Uninstall ----------------

    public static string UninstallConfirm => T("Czy na pewno chcesz odinstalować TabletMonitor wraz ze sterownikiem wirtualnego monitora?", "Do you really want to uninstall TabletMonitor together with its virtual monitor driver?");
    public static string Uninstalled => T("TabletMonitor został odinstalowany.", "TabletMonitor has been uninstalled.");
}
