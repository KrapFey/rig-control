# RigControl plugins

A plugin adds support for mice, keyboards and headsets RigControl doesn't know. It's a separate program, in any
language, that RigControl starts and talks to one line of JSON at a time over its standard input and output. Its
devices then appear on the Devices page with the same controls as the built-in ones.

`plugins/sample-mouse` is a complete plugin in Rust that pretends to be a mouse: start from it.

## Installing one

1. Copy the plugin's folder into the `plugins` folder beside `RigControl.exe` (`C:\Program Files\RigControl\plugins`
   when installed; **Settings → Plugins → Open plugins folder** opens it).
2. Switch it on in **Settings → Plugins**. Every plugin starts off.

Updating RigControl keeps your plugins; uninstalling it removes them.

To try the sample:

```powershell
cargo build --release --manifest-path plugins\sample-mouse\Cargo.toml
```

then copy `plugins\sample-mouse\plugin.json` and `plugins\sample-mouse\target\release\sample-mouse.exe` into a
`sample-mouse` folder in RigControl's plugins folder.

## The folder

```
plugins\
  acme-mouse\
    plugin.json
    acme-mouse.exe        (and anything else it needs)
```

`plugin.json`:

```json
{
  "protocol": 1,
  "id": "acme-mouse",
  "name": "Acme mice",
  "version": "1.2.0",
  "author": "Someone",
  "description": "DPI and polling rate for Acme mice.",
  "program": "acme-mouse.exe",
  "args": [],
  "devices": ["1234:5678", "1234:5679"],
  "admin": false
}
```

| Field | |
|---|---|
| `protocol` | `1`, the protocol below. A plugin for another version isn't started. |
| `id` | Lower-case letters, digits, `.`, `-`, `_`; at most 64. Unique among installed plugins. |
| `name`, `version` | Shown in Settings. A new `version` restarts the plugin. |
| `author`, `description` | Optional, shown in Settings. |
| `program` | An `.exe` inside the plugin's folder. Nothing outside it is started. |
| `args` | Optional arguments (e.g. `["plugin.py"]` with a bundled `python\python.exe` as the program). |
| `devices` | USB ids, `VID:PID` in hex. The plugin is started only while one of them is plugged in (as a HID device). Empty: started at every device scan, to look for devices itself. |
| `admin` | `true` if it can't work without administrator rights. See [Rights](#rights). |

## The protocol

RigControl writes one JSON object per line to the plugin's standard input and reads exactly one line back from its
standard output, one request at a time. UTF-8, no blank lines, nothing else on standard output. Anything the plugin
writes to standard error goes to RigControl's log (`rigcontrol.log`, the first 200 lines). When standard input closes,
RigControl is stopping the plugin: exit.

A request that fails answers `{"error": "what went wrong"}` and the plugin carries on.

### `hello`

Sent once, right after starting.

```
→ {"op":"hello","protocol":1,"app":"RigControl"}
← {"protocol":1}
```

### `discover`

The devices the plugin can control now. Asked when the plugin starts, when a device is plugged in or out, and on
Rescan.

```
→ {"op":"discover"}
← {"devices":[{"key":"1234:5678/serial-1","name":"Acme Mouse Pro","kind":"Mouse","caps":{"dpiMax":26000,"dpiStep":50,"pollRates":[125,500,1000]}}]}
```

- `key`: stable for this device across runs (its saved settings are filed under it); at most 64 characters.
  RigControl adds the plugin id in front, so keys only need to be unique within the plugin.
- `kind`: `Mouse`, `Keyboard` or `Headset` (sets the icon and a few labels).
- `caps`: what the device can do; anything left out isn't offered. At most 32 devices.

### `read`

The device's state now. Asked every 30 seconds, and every second while the Devices page is open.

```
→ {"op":"read","key":"1234:5678/serial-1"}
← {"state":{"dpi":1600,"pollRate":1000,"batteryPercent":82,"charging":false}}
```

When `dpi`, `activeStage` or `gameMode` changes between two reads, RigControl takes it as changed on the device
itself (its DPI button, say) and saves it.

### `apply`

Settings to put on the device: every saved setting for it, not just the one that changed. Apply those that differ
from the device's and ignore any it doesn't have. Sent when the device appears and after each change in RigControl.

```
→ {"op":"apply","key":"1234:5678/serial-1","settings":{"dpi":3200,"pollRate":1000}}
← {"ok":true}
```

### Fields

Names are camelCase. The meanings, ranges and units are those of RigControl's built-in devices
(`src/RigControl.Core/Peripherals.cs`: `PeripheralCaps`, `PeripheralState`, `PeripheralSettings`).

- **caps**: `dpiMin` `dpiMax` `dpiStep` `maxStages` `stageSelectable` `pollRates` `sleepTimeout` `sleepMinSeconds`
  `sleepMaxSeconds` `lowBatteryThreshold` `battery` `charging` · headsets: `sidetone` `inactiveMaxMinutes` `lights`
  `voicePrompts` `rotateToMute` `micVolume` `anc` `chatmix` `eqPresets` · keyboards: `actuationMm` `rapidTrigger`
  `gameMode` `keyswitchOptimization` `fnSwap` `backlight` · on-board lighting: `lighting` `lightingEffects`
  `lightingSpeed` `lightingColor`
- **state**: `dpi` `stages` `activeStage` `pollRate` `sleepSeconds` `lowBatteryPercent` `batteryPercent` `charging`
  `connected` `sidetone` `chatmix` `micMuted` `actuationMm` `rapidTrigger` `gameMode` `keyswitchGaming` `fnSwap`
  `backlight` `lightBrightness` `lightEffect` `lightSpeed` `lightHue` `lightSaturation`
- **settings**: `dpi` `stages` `activeStage` `pollRate` `sleepSeconds` `lowBatteryPercent` `sidetone` `inactiveMinutes`
  `lights` `voicePrompts` `rotateToMute` `micVolume` `eqPreset` `anc` `actuationMm` `rapidTrigger` `gameMode`
  `keyswitchGaming` `fnSwap` `backlight` `lightBrightness` `lightEffect` `lightSpeed` `lightHue` `lightSaturation`

### Time limits and failures

`hello` and `discover` get 10 seconds, `read` and `apply` 3. A plugin that runs out of time, exits, or answers
something that isn't a JSON object is stopped, its devices disappear, and it's started again after 4 seconds, then 8,
16… up to 5 minutes. After 5 failures in a row it stays stopped until it's switched off and on in Settings.

## Rights

RigControl runs as administrator; plugins don't. A plugin runs with an ordinary user's rights (the administrator
group switched off, every privilege removed, medium integrity), in its own folder, and ends with RigControl. Opening
a mouse's, keyboard's or headset's vendor HID interface doesn't need more.

A plugin that can't work without administrator rights says `"admin": true`. It runs that way only after you allow it
when switching it on, and only when RigControl is installed in Program Files, whose files only administrators can
change.

Plugins are code running on your PC: only install and switch on plugins you trust.
