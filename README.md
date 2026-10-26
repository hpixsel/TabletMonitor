# TabletMonitor

Turn an Android tablet or phone into a **real extra monitor** for Windows – an extended desktop, not a mirror – over Wi-Fi or a USB cable.

Everything ships as **one `TabletMonitor.exe`** (the virtual display driver, adb for USB and the Android app are embedded) plus one APK. No Sunshine, Moonlight, spacedesk or extra drivers to install.

## Using it

1. Run `TabletMonitor.exe`, click **Install and start** and approve the single UAC prompt.
   It copies itself to `Program Files`, installs the virtual monitor driver and a small background service, adds a firewall rule and a Start menu entry.
2. Get the app onto the tablet: *Save the installer (APK)* in the PC app – or plug the tablet in via USB with USB debugging enabled and the PC installs it for you.
3. Open the app on the tablet and tap your PC – or click **Connect** on the PC. After the first approval the tablet connects automatically.

The extra screen exists **only while a tablet is connected**. Disconnecting, *Turn off screen*, the tray menu, closing the app or losing the connection removes it, and windows move back to your other monitors. Between sessions the virtual display adapter is switched off entirely, so it does not linger in Windows display settings either.

## Features

- Extended desktop at the tablet's native resolution (or half of it for smoother streaming), 30–144 Hz
- Automatic display scaling from the tablet's pixel density
- Native Windows touch input – tap, scroll, pinch zoom, press-and-hold for right click
- Portrait mode – the Windows display rotates with the tablet
- Discovery on the local network, auto-connect for trusted tablets, approval of new tablets on the PC
- USB connection through the built-in adb
- Hardware H.264 encoding on the GPU (AMD / NVIDIA / Intel via Media Foundation) and hardware decoding on the tablet; the mouse cursor travels as a separate layer
- Polish and English UI on both sides (follows the system language, can be changed in settings), light and dark theme

## Requirements

- Windows 10 2004 or newer (x64), Windows 11 recommended
- Android 8.0 or newer

## Repository layout

| Path | Contents |
|---|---|
| `pc/TabletMonitor.Host` | Windows app (.NET 10, WPF with the Fluent theme) – self-contained single exe |
| `android/` | Android app (Kotlin, Jetpack Compose, Material 3) |
| `docs/PROTOCOL.md` | PC ↔ tablet protocol |
| `third_party/VirtualDisplayDriver` | Signed Indirect Display Driver ([VirtualDrivers/Virtual-Display-Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver), MIT) |

## Building

Requirements: .NET 10 SDK, JDK 17+, Android SDK (path in `android/local.properties` or `ANDROID_HOME`).

```powershell
./build.ps1               # build\TabletMonitor.exe (with the APK embedded) + build\TabletMonitor.apk
./build.ps1 -SkipAndroid  # PC app only, reusing build\TabletMonitor.apk
```

The first build downloads adb (Android SDK Platform-Tools) from Google into `third_party/adb`.
The APK is signed with the key in `android/signing/` (not in git: `signing.properties` with `storeFile`, `storePassword`, `keyAlias`, `keyPassword`); without it the debug key is used.

## Diagnostics

- Log: `%LOCALAPPDATA%\TabletMonitor\log.txt` (*Event log* link in the app)
- `TabletMonitor.exe --selftest out.h264 [W H Hz seconds]` – turns on the virtual display, records the encoded stream to a file and turns it off again (driver + capture + encoder without a tablet)
- `TabletMonitor.exe --touchtest out.txt` – injects a tap, a drag and a stylus tap into a test window on the virtual display
- `TabletMonitor.exe --uninstall` – removes the app, the service and the driver (also available in *Settings → Apps*)

## License

[MIT](LICENSE). Third-party components are listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
