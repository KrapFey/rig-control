# RigControl

<img src="src/RigControl/Assets/RigControl.png" width="72" align="right">

An open-source Windows app for your PC's fans and RGB lighting. Build temperature-based fan curves for
motherboard, GPU, fan-controller and AIO fans, light every RGB device in sync, and watch it all live in a
modern WinUI 3 interface.

## Features

- **Fans:** every motherboard fan chip, CPU and GPU that
  [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) supports, plus a
  native driver for the NZXT HUE 2 family (Smart Device V2, RGB & Fan Controller, 3+6 channel models).
- **More controllers and AIOs through [liquidctl](https://github.com/liquidctl/liquidctl):** Corsair Commander
  and Hydro, NZXT Kraken and Smart Device, Asetek-based coolers, ASUS Ryujin, Lian Li Uni hubs, MSI coolers and more.
  - A bundled copy runs as a small helper, started only when such a device is plugged in.
  - Pumps never go below 50%.
  - Lighting-only devices are never touched.
  - Any NZXT HUE 2 controller can be switched to liquidctl's driver in **Settings → Fan controller drivers**.
- **Curves:** graph curves you drag in place, mix (max/min/average of other curves) and flat curves.
  Deadband and response time per curve; minimum, start, stop and step limits per fan.
- **Safe by default:**
  - A fan whose temperature sensor disappears goes to 100%.
  - On exit or crash every fan goes back to the hardware's own control. NZXT controllers hold the last speed they were given, so they're set to a safe 60%.
  - RigControl stays hands-off while FanControl is running.
- **Lighting:** hundreds of RGB devices (RAM, GPUs, motherboards, keyboards, mice, controllers…) through a
  bundled, headless [OpenRGB](https://codeberg.org/OpenRGB/OpenRGB) 1.1, plus a native Razer Seiren V3 Chroma driver.
  - **Synced effects across every device:** rainbow wave, colour cycle, breathing, static and gradients.
  - **Temperature lighting:** colour follows any sensor.
  - **Per device:** keep the synced effect, use one of the device's own effects, or turn it off.
  - **Profiles,** live LED previews, and **Identify**, which pulses one device.
  - RigControl steps aside while another OpenRGB is running.
- **Devices:** a page per mouse, keyboard and headset, with live battery in the tray tooltip.
  - **Mice:** 115 Razer mice: DPI (type it, step it, presets or slider), on-device DPI stages that follow the
    mouse's DPI button live, polling rate, sleep and low-battery thresholds.
  - **Logitech mice** (wired, Lightspeed, Unifying, Bolt) over HID++ 2.0: DPI, report rate, battery. The mouse stays
    in its onboard-profile mode, so its own DPI button keeps working.
  - **Headsets:** about 50 headsets through a bundled [HeadsetControl](https://github.com/Sapd/HeadsetControl):
    battery, sidetone, mic volume, auto-off, EQ preset, noise cancelling, lights, voice prompts, rotate to mute.
  - **VIA keyboards** (QMK firmware with VIA, protocol 12+: Keychron and many others): lighting brightness,
    effect, speed and colour, asked from the keyboard itself, set live.
  - **Keyboards with adjustable switches:** actuation depth for every analog key on the SteelSeries Apex Pro
    TKL (2023, wired) and Apex Pro Gen 3 (firmware 4.16.8), plus rapid trigger on the TKL (2023). Only live settings
    are written, never the keyboard's saved profiles.
- **Supported devices:** a searchable list (fuzzy: "dthaddr v2" finds the DeathAdder V2) of every device and
  device family RigControl handles, built by `tools/gen-supported-devices.py` from each source's own data.
- **Import from FanControl:** curves, fan names and fan settings, including NZXT controllers driven through
  the FanControl.Liquidctl plugin (matched by the plugin's own device ids).
- **Identify** stops one fan for 8 seconds so you can see which it is; **Swap with…** exchanges two fans'
  settings.
- Tray icon, start with Windows (scheduled task, admin without a UAC prompt), Mica, live animations.

## Requirements

- Windows 10 1904x or Windows 11, x64.
- [PawnIO](https://pawnio.eu) for motherboard fans and CPU temperatures (`winget install namazso.PawnIO`).
- RigControl runs as administrator (hardware access).

## Install

1. Install [PawnIO](https://pawnio.eu) (`winget install namazso.PawnIO`).
2. Download `rig-control-<version>-setup.exe` from [Releases](https://github.com/KrapFey/rig-control/releases) and run it.
   It installs for your user only (no admin prompt) into `%LOCALAPPDATA%\Programs\RigControl` and adds a Start
   menu shortcut. RigControl asks for admin rights when it starts. Prefer no installer? The
   `rig-control-<version>-win-x64.zip` is the same app: extract it somewhere permanent.
3. Optional:
   - **Import FanControl setup** on the dashboard brings over your curves and fan names.
   - **Settings → Start with Windows** starts RigControl in the tray at logon.

RigControl checks GitHub for a newer release at start and once a day, and offers to update in one click (the
installer is checked against the SHA-256 GitHub publishes for it). Turn it off in **Settings → Updates**.
Uninstalling keeps your settings unless you choose to remove them.

If another fan app is running, exit it first. RigControl stays hands-off while FanControl runs.
## Build

Needs the .NET 10 SDK, and Python 3.13 x64 to build the bundled liquidctl runtime. Without `build\liquidctl`,
`build\openrgb` or `build\headsetcontrol` the app still builds; those features are then unavailable.

```powershell
tools\fetch-liquidctl.ps1                                    # build\liquidctl: Python + liquidctl, SHA-256 pinned (optional)
tools\fetch-openrgb.ps1                                      # build\openrgb: OpenRGB 1.1 engine, SHA-256 pinned (optional)
tools\fetch-headsetcontrol.ps1                               # build\headsetcontrol: HeadsetControl 4.2.0, SHA-256 pinned (optional)
dotnet run --project tests\RigControl.Checks                 # self-checks
dotnet build src\RigControl                                  # app (asks for admin when started)
tools\build-release.ps1                                      # build\release: app folder, zip and installer (needs Inno Setup 6)
```

Read-only hardware diagnostics (admin for `--probe` / `--lhm`):

```powershell
tests\RigControl.Checks\bin\Debug\net10.0-windows10.0.26100.0\RigControl.Checks.exe --probe   # all sensors and fan channels
tests\RigControl.Checks\bin\Debug\net10.0-windows10.0.26100.0\RigControl.Checks.exe --watch   # NZXT duty/RPM for 8 s
tests\RigControl.Checks\bin\Debug\net10.0-windows10.0.26100.0\RigControl.Checks.exe --lqprobe # would the liquidctl helper start here?
tests\RigControl.Checks\bin\Debug\net10.0-windows10.0.26100.0\RigControl.Checks.exe --logitech # Logitech mice found and their state
tests\RigControl.Checks\bin\Debug\net10.0-windows10.0.26100.0\RigControl.Checks.exe --via      # VIA keyboards found and their lighting
```

`-p:UiTest=true` builds a copy that runs without admin and never touches fans, for UI automation
(`tools\ui.ps1`, `tools\shot.ps1`, `tools\tray.ps1`).

Settings live in `%APPDATA%\RigControl\config.json`; every load and save is logged to `rigcontrol.log` beside it.

## Credits and licence

GPL-3.0-or-later, see [LICENSE](LICENSE).

- [OpenRGB](https://codeberg.org/OpenRGB/OpenRGB) 1.1 (GPL-2.0): bundled unmodified as a separate program; its
  licence and source link ship beside it (`openrgb\SOURCE.txt`).
- [HeadsetControl](https://github.com/Sapd/HeadsetControl) 4.2.0 (GPL-3.0): bundled unmodified as a separate
  program, with its licence and source link (`headsetcontrol\SOURCE.txt`).
- Logitech HID++ 2.0 protocol details: from [Solaar](https://github.com/pwr-Solaar/Solaar) (GPL-2.0-or-later) and
  [libratbag](https://github.com/libratbag/libratbag) (MIT).
- SteelSeries Apex Pro keyboard protocols: TKL (2023) frames as documented by
  [OpenMouse keyboard-protocol](https://github.com/OpenMouse-Project/keyboard-protocol) (reimplemented, no code
  copied) and confirmed by [steelseriesgg-rs](https://github.com/Ven0m0/steelseriesgg-rs); Gen 3 commands and
  actuation tables from [apex-analog](https://github.com/milescoviello/apex-analog) (MIT).
- VIA protocol: [QMK firmware](https://github.com/qmk/qmk_firmware) `quantum/via.h` (GPL-2.0-or-later).
- Razer mouse protocol and device table: from [openrazer](https://github.com/openrazer/openrazer) (GPL-2.0-or-later).
- Razer Seiren V3 Chroma lighting protocol: from [openrazer PR #2896](https://github.com/openrazer/openrazer/pull/2896).
- [liquidctl](https://github.com/liquidctl/liquidctl) (GPL-3.0-or-later): bundled, and the source of the
  NZXT HUE 2 fan protocol; bundled with [Python](https://www.python.org) (PSF licence) and its dependencies
  (hidapi, pyusb, libusb, Pillow and others, under their own licences).
- [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0),
  [HidSharp](https://www.zer7.com/software/hidsharp) (Apache-2.0),
  [H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon) (MIT).

Not affiliated with NZXT, Razer, Logitech, SteelSeries, OpenRGB, FanControl or any hardware vendor.
