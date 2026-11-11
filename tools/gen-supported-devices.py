"""Builds src/RigControl.Core/supported-devices.json: every device RigControl knows how to handle, for the app's
searchable Supported devices page.

Sources (read as text, nothing from them is run):
  - OpenRGB 1.1 source at the exact commit RigControl bundles: its device detector names (lighting)
  - liquidctl drivers from the bundled runtime (build\\liquidctl): fan/pump devices the bridge uses
  - HeadsetControl 4.2.0 source: headset names
  - razer-mice.json (generated from openrazer by gen-razer-devices.py)
  - RigControl's own drivers (NZXT HUE 2, SteelSeries, Seiren) and families matched by protocol, listed by hand

Run: python -I tools/gen-supported-devices.py   (needs build\\liquidctl from tools\\fetch-liquidctl.ps1)
"""
import ast
import io
import json
import pathlib
import re
import tarfile
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parent.parent
OUT = ROOT / "src" / "RigControl.Core" / "supported-devices.json"
OPENRGB = "https://codeberg.org/OpenRGB/OpenRGB/archive/8e16de27858cf2be6af54a33ce003df74cb9fd3e.tar.gz"
HEADSETCONTROL = "https://codeload.github.com/Sapd/HeadsetControl/tar.gz/refs/tags/4.2.0"

# Must match FAN_MODULES in src/RigControl.Core/liquidctl/bridge.py.
FAN_MODULES = {
    "smart_device", "kraken2", "kraken3", "commander_pro", "commander_core", "hydro_platinum",
    "asetek", "asetek_pro", "coolit", "aquacomputer", "asus_ryujin", "asus_ryuo", "lianli_uni",
    "msi", "control_hub", "corsair_hid_psu", "nzxt_epsu",
}


def fetch_tar(url):
    with urllib.request.urlopen(url, timeout=120) as r:
        return tarfile.open(fileobj=io.BytesIO(r.read()), mode="r:gz")


def text_files(tar, part, suffixes):
    for m in tar.getmembers():
        if m.isfile() and part in m.name and m.name.endswith(suffixes):
            yield tar.extractfile(m).read().decode("utf-8", "replace")


entries = {}


def add(name, kind, features, source):
    """One row per device name: a mouse that OpenRGB also lights gets "Mouse · Lighting", features and sources merged."""
    name = re.sub(r"\s+", " ", name).strip()
    if not name or "test device" in name.lower() or "(broken)" in name.lower():
        return
    e = entries.setdefault(name.lower(), {"name": name, "kind": kind, "features": features, "source": source})
    for field, value, sep in (("kind", kind, " · "), ("features", features, ", "), ("source", source, " · ")):
        items = e[field].split(sep)
        items += [v for v in value.split(sep) if v not in items]
        e[field] = sep.join(items)


# --- fans and pumps: liquidctl drivers in the bundled runtime
drivers = next((ROOT / "build" / "liquidctl").rglob("liquidctl/driver"), None)
if drivers is None:
    raise SystemExit("build\\liquidctl not found: run tools\\fetch-liquidctl.ps1 first")
for py in sorted(drivers.glob("*.py")):
    if py.stem not in FAN_MODULES:
        continue
    for node in ast.walk(ast.parse(py.read_text(encoding="utf-8"))):
        if isinstance(node, ast.Assign) and any(isinstance(t, ast.Name) and t.id == "_MATCHES" for t in node.targets):
            for item in getattr(node.value, "elts", []):
                for el in getattr(item, "elts", []):
                    if isinstance(el, ast.Constant) and isinstance(el.value, str):
                        add(el.value, "Fans & cooling", "fan and pump speed", "liquidctl")
                        break

# --- headsets: HeadsetControl
name_after = re.compile(r'getDeviceName[^{]*\{[^"]*"([^"]+)"', re.S)
for src in text_files(fetch_tar(HEADSETCONTROL), "/lib/devices/", (".hpp", ".h")):
    for name in name_after.findall(src):
        add(name, "Headset", "battery, sidetone and more (per headset)", "HeadsetControl 4.2.0")

# --- mice: Razer table
razer = json.loads((ROOT / "src" / "RigControl.Core" / "Razer" / "razer-mice.json").read_text(encoding="utf-8"))
for d in razer["devices"].values():
    feats = ["DPI"] if "dpi_write" in d else []
    feats += ["DPI stages"] if "stages_write" in d else []
    feats += ["polling rate"] if "poll_write" in d else []
    feats += ["battery"] if "battery" in d else []
    add(d["name"], "Mouse", ", ".join(feats) or "detected", "Razer protocol (openrazer data)")

# --- RigControl's own drivers and protocol families
for name in ["NZXT Smart Device V2", "NZXT RGB & Fan Controller", "NZXT RGB & Fan Controller (3+6 channels)"]:
    add(name, "Fans & cooling", "fan speed (built-in driver)", "RigControl")
add("Motherboard fan headers (Nuvoton, ITE, Fintek and other Super I/O chips)", "Fans & cooling", "fan speed, temperatures", "LibreHardwareMonitor")
add("NVIDIA and AMD graphics cards", "Fans & cooling", "fan speed, temperatures", "LibreHardwareMonitor")
add("CPUs (Intel and AMD)", "Fans & cooling", "temperatures", "LibreHardwareMonitor")
add("Razer Seiren V3 Chroma", "Lighting", "RGB lighting (built-in driver)", "RigControl")
add("Logitech mice and keyboards (HID++ 2.0: wired, Lightspeed, Unifying, Bolt)", "Mouse", "DPI, report rate, battery", "Logitech HID++ 2.0")
add("SteelSeries Apex Pro TKL (2023, wired)", "Keyboard", "actuation depth, rapid trigger", "SteelSeries protocol")
add("SteelSeries Apex Pro Gen 3 (firmware 4.16.8)", "Keyboard", "actuation depth", "SteelSeries protocol")
add("QMK keyboards with VIA (protocol 12+): Keychron and many others", "Keyboard", "lighting brightness, effect, speed, colour", "VIA")

# --- lighting: OpenRGB detectors
detector = re.compile(r'REGISTER_[A-Z0-9_]*DETECTOR[A-Z0-9_]*\s*\(\s*"([^"]+)"')
for src in text_files(fetch_tar(OPENRGB), "/Controllers/", (".cpp", ".h")):
    for name in detector.findall(src):
        add(name, "Lighting", "RGB lighting", "OpenRGB 1.1")

out = sorted(entries.values(), key=lambda e: (e["kind"], e["name"].lower()))
OUT.write_text(json.dumps({"generated_by": "tools/gen-supported-devices.py", "devices": out}, indent=0, ensure_ascii=False) + "\n", encoding="utf-8")
by_kind = {}
for e in out:
    for k in e["kind"].split(" · "):
        by_kind[k] = by_kind.get(k, 0) + 1
print(f"{len(out)} entries -> {OUT}: " + ", ".join(f"{k} {v}" for k, v in sorted(by_kind.items())))
