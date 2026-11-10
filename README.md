# RigControl

<img src="src/RigControl/Assets/RigControl.png" width="72" align="right">

An open-source Windows app for controlling your PC's fans. Build temperature-based fan curves for
motherboard, GPU, fan-controller and AIO fans, and watch it all live in a modern WinUI 3 interface.

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
2. Download `rig-control-<version>-win-x64.zip` from [Releases](https://github.com/KrapFey/rig-control/releases)
   and extract it somewhere permanent, e.g. `%LOCALAPPDATA%\Programs\RigControl`.
3. Run `RigControl.exe` and accept the admin prompt.
4. Optional:
   - **Import FanControl setup** on the dashboard brings over your curves and fan names.
   - **Settings → Start with Windows** starts RigControl in the tray at logon.

If another fan app is running, exit it first. RigControl stays hands-off while FanControl runs.

## Build

Needs the .NET 10 SDK, and Python 3.13 x64 to build the bundled liquidctl runtime.

```powershell
tools\fetch-liquidctl.ps1                                    # build\liquidctl: Python + liquidctl, SHA-256 pinned (optional)
dotnet run --project tests\RigControl.Checks                 # self-checks
dotnet build src\RigControl                                  # app (asks for admin when started)
dotnet publish src\RigControl -c Release -r win-x64 --self-contained -o "$env:LOCALAPPDATA\Programs\RigControl"
```

Read-only hardware diagnostics (admin for `--probe` / `--lhm`):

```powershell
tests\RigControl.Checks\bin\Debug\net10.0-windows10.0.26100.0\RigControl.Checks.exe --probe   # all sensors and fan channels
tests\RigControl.Checks\bin\Debug\net10.0-windows10.0.26100.0\RigControl.Checks.exe --watch   # NZXT duty/RPM for 8 s
tests\RigControl.Checks\bin\Debug\net10.0-windows10.0.26100.0\RigControl.Checks.exe --lqprobe # would the liquidctl helper start here?
```

`-p:UiTest=true` builds a copy that runs without admin and never touches fans, for UI automation
(`tools\ui.ps1`, `tools\shot.ps1`, `tools\tray.ps1`).

Settings live in `%APPDATA%\RigControl\config.json`; every load and save is logged to `rigcontrol.log` beside it.

## Credits and licence

GPL-3.0-or-later, see [LICENSE](LICENSE).

- [liquidctl](https://github.com/liquidctl/liquidctl) (GPL-3.0-or-later): bundled, and the source of the
  NZXT HUE 2 fan protocol; bundled with [Python](https://www.python.org) (PSF licence) and its dependencies
  (hidapi, pyusb, libusb, Pillow and others, under their own licences).
- [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0),
  [HidSharp](https://www.zer7.com/software/hidsharp) (Apache-2.0),
  [H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon) (MIT).

Not affiliated with NZXT, FanControl or any hardware vendor.
