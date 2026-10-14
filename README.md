<div align="center">
  <img src="BatteryMonitor/Resources/AppIcon.ico" width="64" height="64" alt="Battery Monitor Icon"/>
  <h1>Battery Monitor</h1>
  <p>A lightweight, real-time battery dashboard for Windows laptops.<br/>
  Click-through corner overlay · Self-learning time estimates · Dark &amp; light theme · 12 languages · No installation required.</p>
  <p><sub>A fork of <a href="https://github.com/lucatze/BatteryMonitor">lucatze/BatteryMonitor</a>.</sub></p>

  ![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue)
  ![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)
  ![Language](https://img.shields.io/badge/language-C%23%20%2F%20WPF-239120)
  ![License](https://img.shields.io/badge/license-MIT-green)
</div>

---

## Screenshot

<div align="center">
  <img src="docs/screenshot.png" width="420" alt="Battery Monitor Screenshot"/>
</div>

---

## Features

- **Overlay mode** — a small always-on-top readout in a screen corner, like an FPS counter. It is click-through (the mouse passes straight through it), and you choose what it shows and which corner it sits in. Toggle it anywhere with **Ctrl+Alt+O**, or start with `--overlay`
- **Self-learning time left** — checks its own forecasts against what actually happens, learns how much a change in load lasts, and counts down to the level where Windows hibernates
- **Learned charging time** — understands that charging slows near full
- **Where the power goes** — while plugged in, laptops don't report how much the charger delivers, so the app learns your system's draw from battery use and shows the system load as an estimate. A "Top power users" list shows which apps use the most
- **Steadier battery health** — from Windows' own battery history, not a single wobbly reading
- **Live battery data** — charge level, health, voltage, charge/discharge rate, smoothed and cross-checked
- **Runtime estimate** — time until full or time remaining on battery
- **Capacity overview** — design capacity vs. actual full-charge capacity
- **Device info** — model, manufacturer, serial number via WMI
- **System tray icon** — shows charge %, watt, or mWh — configurable
- **Taskbar badge** — overlay on the taskbar button, color-coded by charge level
- **Dark & light theme** — toggle in the title bar
- **12 languages** — auto-detects system language on first launch
- **Portable** — single `.exe`, no installer, no registry entries

---

## Languages

| | | | | |
|---|---|---|---|---|
| 🇬🇧 English | 🇫🇷 Français | 🇪🇸 Español | 🇮🇹 Italiano | 🇵🇹 Português |
| 🇷🇴 Română | 🇵🇱 Polski | 🇳🇱 Nederlands | 🇹🇷 Türkçe | 🇷🇺 Русский |
| 🇨🇳 中文 | 🇯🇵 日本語 | | | |

Language is auto-detected from Windows system settings on first launch and can be changed at any time via the title bar button.

---

## Download

Go to [**Releases**](../../releases/latest) and pick the right version:

**Easiest:** download the installer for your PC (`win-x64` for most PCs, `win-arm64` for ARM). It installs for your user only (no admin needed), adds a Start-menu entry, can optionally start with Windows, and uninstalls cleanly from *Settings → Apps*.

| File | Description |
|---|---|
| `BatteryMonitor-win-x64-setup.exe` | **Installer** · 64-bit · No .NET required |
| `BatteryMonitor-win-arm64-setup.exe` | **Installer** · ARM64 · No .NET required |
| `BatteryMonitor-win-x64-standalone.exe` | 64-bit · No .NET required · ~155 MB |
| `BatteryMonitor-win-x64-net8.exe` | 64-bit · Requires [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) · ~1 MB |
| `BatteryMonitor-win-arm64-standalone.exe` | ARM64 · No .NET required · ~140 MB |
| `BatteryMonitor-win-arm64-net8.exe` | ARM64 · Requires [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) · ~1 MB |

> **Not sure which to pick?**
> Use `win-x64-net8` if .NET 8 is already installed (most development machines).
> Use `win-x64-standalone` for a just-works portable EXE on any Windows 10/11 PC.

---

## Requirements

- Windows 10 or Windows 11
- x64 or ARM64 processor
- Battery with a standard ACPI driver (all modern laptops)
- .NET 8 Desktop Runtime — only for the `-net8` variants

---

## Build from Source

```bash
# Prerequisites: .NET 8 SDK

git clone https://github.com/MTMAG11/BatteryMonitor.git
cd BatteryMonitor/BatteryMonitor

# Run in debug
dotnet run

# Build release
dotnet build -c Release

# Publish single-file EXE (standalone)
dotnet publish BatteryMonitor.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o dist
```

---

## Making a release

1. Bump `<Version>` in `BatteryMonitor/BatteryMonitor.csproj` (patch for fixes, minor for features, major for breaking changes).
2. Add a section for that version at the top of [CHANGELOG.md](CHANGELOG.md).
3. Commit and push.
4. Tag it and push the tag — the tag must be `v` plus the version, e.g. `v1.2.0`:
   ```bash
   git tag -a v1.2.0 -m "v1.2.0"
   git push origin v1.2.0
   ```
5. GitHub Actions ([release.yml](.github/workflows/release.yml)) builds all four exes and publishes the release with the changelog notes attached.

To build everything yourself instead, run `./release.ps1` (add `-X64Only` for a quick test). Files appear in `release/`, with a `SHA256SUMS.txt`, ready to upload to a release by hand. The installers are built with [Inno Setup](https://jrsoftware.org/isinfo.php) (free); `installer/BatteryMonitor.iss` is the script. Without Inno Setup installed, `release.ps1` skips them and still builds the exes.

---

## Where it keeps its data

Everything stays on your machine in `%AppData%\BatteryMonitor`: your settings, what it has learned about your laptop (system load, time-left and charging models), and a cached copy of Windows' battery history. Delete that folder, or press **Reset learning**, to start fresh. A `runtime_log.csv` there records how its time-left forecasts did.

---

## Tech Stack

| | |
|---|---|
| Language | C# 12 |
| Framework | .NET 8 · WPF |
| Battery data | WMI (`root\wmi`) |
| Tray icon | [Hardcodet.NotifyIcon.Wpf](https://github.com/hardcodet/wpf-notifyicon) |
| UI pattern | Code-behind (no MVVM framework) |
| Settings | `%AppData%\BatteryMonitor\settings.json` |

---

## License

MIT — see [LICENSE](LICENSE)


