# Changelog

All notable changes to this fork are listed here. The newest version is on top.

How to add a release: bump `<Version>` in `BatteryMonitor/BatteryMonitor.csproj`, add a section below with the
same number, commit, then tag it `v<number>` (see the README, "Making a release").
Version numbers follow `MAJOR.MINOR.PATCH`: bump PATCH for bug fixes, MINOR for new features, MAJOR for breaking changes.

## [1.0.1] - 2026-10-14

### Removed
- The Top power users list: its per-app power numbers were too inaccurate to be useful.

## [1.0.0] - 2026-10-14

### Added
- **Overlay mode.** A small always-on-top readout in a screen corner, like an FPS counter. It is click-through, so
  the mouse never interacts with it. Choose it at launch, with the `--overlay` flag, or from the tray menu.
- **Overlay options:** pick what it shows (charge %, watts, time left, voltage, health, system load, charge and
  health circles) and which corner it sits in. Global hotkey **Ctrl+Alt+O** toggles it from anywhere.
- **Single instance:** launching the exe again brings up the running copy's window instead of starting a second one.
- **Learned time-left estimate.** It checks its own forecasts against what actually happens, learns how much of a
  change in power draw lasts, counts down to the level where Windows hibernates (read from your power plan) and
  keeps improving across sessions.
- **Learned charging time** that understands charging slows near full.
- **Learned system load while plugged in** (from CPU power, GPU load and screen brightness), because laptops don't
  report how much the charger delivers. Shown as an estimate.
- **Top power users:** a list of the apps using the most power, by name.
- **Health from Windows' battery history**, which is steadier than the gauge's own reading, plus charge cycles.
- **Reset learning** button to clear everything it has learned.
- **Windows installers** (`BatteryMonitor-win-x64-setup.exe` and `-arm64-`): per-user install with no admin prompt,
  Start-menu entry, optional desktop shortcut and "start with Windows", clean upgrade and uninstall.
- `--exit` command-line flag that asks a running copy to quit cleanly (the installer uses it).

### Changed
- Battery readings are smoothed and cross-checked: noisy watt readings are averaged, a missing rate is worked out
  from how fast the capacity changes, impossible values are ignored, several batteries are added together, and
  the percentage matches Windows' own.
- Readings are taken in the background, so the window no longer stalls during a WMI query.

### Removed
- The German translation (12 languages remain).

### Fixed
- The app could not be closed by Windows during shutdown or by an installer, because closing the window only hid it.
  It now quits properly in those cases (the window's close button still hides it to the tray).
- The first reading used to be lost at startup, so values took a few seconds to appear.
- Error messages are now in English.

---

This project is a fork of [lucatze/BatteryMonitor](https://github.com/lucatze/BatteryMonitor) and builds on its original 1.0.0 release.
The version numbers here start again at 1.0.0 for this fork.

