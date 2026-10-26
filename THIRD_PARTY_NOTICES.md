# Third-party notices

TabletMonitor itself is licensed under the [MIT License](LICENSE). It bundles or depends on the following components.

## Bundled in `TabletMonitor.exe`

| Component | License | Notes |
|---|---|---|
| [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) (MikeTheTech) | MIT – see [third_party/VirtualDisplayDriver/LICENSE](third_party/VirtualDisplayDriver/LICENSE) | Signed Indirect Display Driver package, stored in this repository |
| adb from [Android SDK Platform-Tools](https://developer.android.com/tools/releases/platform-tools) | Apache License 2.0 and others – see `NOTICE.txt` shipped with Platform-Tools | **Not stored in this repository**; `build.ps1` downloads it from Google |
| [.NET runtime](https://github.com/dotnet/runtime) and [WPF](https://github.com/dotnet/wpf) | MIT | Self-contained single-file publish |
| [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) (Direct3D 11, DXGI, Media Foundation bindings) | MIT | NuGet |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MIT | NuGet |
| [System.ServiceProcess.ServiceController](https://github.com/dotnet/runtime) | MIT | NuGet |

## Android app

| Component | License |
|---|---|
| [Kotlin](https://kotlinlang.org/) standard library and coroutines | Apache License 2.0 |
| [AndroidX](https://developer.android.com/jetpack/androidx) – Core, Activity, Lifecycle, Jetpack Compose, Material 3, Material Icons | Apache License 2.0 |
