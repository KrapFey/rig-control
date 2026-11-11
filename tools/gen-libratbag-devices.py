"""Builds src/RigControl.Core/libratbag-mice.json from libratbag's device data files (github.com/libratbag/libratbag,
MIT): the mice of the drivers RigControl ports, with their USB ids, DPI range, preset count and quirks.

Reads the .device files as text (nothing is run). Usage:
  python -I tools/gen-libratbag-devices.py [path to a libratbag checkout; default: downloads the pinned commit]
"""
import configparser
import io
import json
import pathlib
import sys
import tarfile
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parent.parent
OUT = ROOT / "src" / "RigControl.Core" / "libratbag-mice.json"
COMMIT = "8235b5bb6032dea15901ab58a8218956f4494d08"  # libratbag master when this table was made
URL = f"https://codeload.github.com/libratbag/libratbag/tar.gz/{COMMIT}"
DRIVERS = {"asus", "hidpp10"}


def device_files():
    if len(sys.argv) > 1:
        for p in sorted(pathlib.Path(sys.argv[1], "data", "devices").glob("*.device")):
            yield p.name, p.read_text(encoding="utf-8")
        return
    with urllib.request.urlopen(URL, timeout=120) as r:
        tar = tarfile.open(fileobj=io.BytesIO(r.read()), mode="r:gz")
    for m in sorted(tar.getmembers(), key=lambda m: m.name):
        if m.isfile() and "/data/devices/" in m.name and m.name.endswith(".device"):
            yield m.name.rsplit("/", 1)[1], tar.extractfile(m).read().decode("utf-8")


mice = []
for name, text in device_files():
    cfg = configparser.ConfigParser(interpolation=None)
    cfg.optionxform = str
    cfg.read_string(text)
    dev = cfg["Device"]
    driver = dev.get("Driver", "")
    if driver not in DRIVERS or dev.get("DeviceType", "mouse") != "mouse":
        continue
    d = cfg[f"Driver/{driver}"] if cfg.has_section(f"Driver/{driver}") else {}
    quirks = [q for q in d.get("Quirks", "").split(";") if q]
    if "OMNI_RECEIVER" in quirks:
        continue  # the receiver needs an identification step RigControl doesn't port
    ids = []
    for match in dev["DeviceMatch"].split(";"):
        bus, vid, pid = match.split(":")
        if bus == "usb":
            ids.append([int(vid, 16), int(pid, 16)])
    entry = {
        "name": dev["Name"].replace(" Mouse", "").strip(),
        "driver": driver,
        "ids": ids,
        "quirks": quirks,
        "file": name,
    }
    if driver == "hidpp10":
        # Only mice with DPI data: the current-resolution register is all RigControl uses.
        if "DpiList" in d:
            entry["dpiList"] = [int(x) for x in d["DpiList"].split(";") if x]
        elif "DpiRange" in d:
            lo, rest = d["DpiRange"].split(":")
            hi, step = rest.split("@")
            entry["dpiRange"] = [int(lo), int(hi), float(step)]
        else:
            continue
        entry["profileType"] = d.get("ProfileType", "")
        entry["deviceIndex"] = int(d.get("DeviceIndex", 255))
    else:
        lo, rest = d.get("DpiRange", "100:16000@100").split(":")
        hi, step = rest.split("@")
        entry["presets"] = int(d.get("Dpis", 2))
        entry["dpi"] = [int(lo), int(hi), int(float(step))]
    mice.append(entry)

OUT.write_text(json.dumps({"generated_by": "tools/gen-libratbag-devices.py", "source": f"libratbag {COMMIT} data/devices",
                           "mice": mice}, indent=1) + "\n", encoding="utf-8")
print(f"{len(mice)} mice -> {OUT}")
