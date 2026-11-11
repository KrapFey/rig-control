"""RigControl's helper for SteelSeries mice, through rivalcfg (github.com/flozz/rivalcfg). One call per run,
JSON on stdout:

  list                       -> [{"vid", "pid", "name", "stages", "presets", "dpi", "poll", "sleep", "battery"}]
  battery <vid> <pid>        -> {"level", "charging"}
  apply <vid> <pid> <json>   -> {"ok", "errors"}    json: {"stages": [..], "active": n, "poll": hz, "sleep": minutes}

Settings are sent live only: rivalcfg's own settings file is never written (a stub replaces it) and the mouse's
flash save command is never sent. RigControl applies its settings again whenever the mouse appears.
"""
import json
import sys

from rivalcfg import devices, usbhid
from rivalcfg.mouse import Mouse


class NoSettings:
    """Stands in for rivalcfg's settings file: RigControl keeps the settings itself."""

    def set(self, name, value):
        pass

    def get(self, name):
        return None

    def save(self):
        pass


def caps(profile):
    s = profile["settings"]
    out = {"vid": profile["vendor_id"], "pid": profile["product_id"], "name": profile["name"],
           "stages": 0, "presets": "none", "dpi": None, "poll": [], "sleep": None,
           "battery": "battery_level" in profile}
    if "sensitivity" in s and s["sensitivity"]["value_type"].startswith("multidpi"):
        out.update(stages=s["sensitivity"]["max_preset_count"], presets="list", dpi=s["sensitivity"]["input_range"])
    elif "sensitivity1" in s and "sensitivity2" in s:
        out.update(stages=2, presets="pair", dpi=s["sensitivity1"]["input_range"])
    if "polling_rate" in s and s["polling_rate"]["value_type"] == "choice":
        out["poll"] = sorted(k for k in s["polling_rate"]["choices"] if isinstance(k, int))
    if "sleep_timer" in s and s["sleep_timer"]["value_type"] == "range":
        out["sleep"] = s["sleep_timer"]["input_range"][:2]
    return out


def open_mouse(vid, pid):
    profile = devices.get_profile(vendor_id=vid, product_id=pid)
    return Mouse(usbhid.open_device(vid, pid, profile["endpoint"]), profile, NoSettings())


def main(args):
    if args[0] == "list":
        ids = sorted({(d["vendor_id"], d["product_id"]) for d in devices.list_plugged_devices()})
        return [caps(devices.get_profile(vendor_id=v, product_id=p)) for v, p in ids]
    vid, pid = int(args[1]), int(args[2])
    mouse = open_mouse(vid, pid)
    try:
        if args[0] == "battery":
            b = mouse.battery
            return {"level": b.get("level"), "charging": b.get("is_charging")}
        if args[0] == "apply":
            want, errors = json.loads(args[3]), {}
            c = caps(mouse.mouse_profile)

            def attempt(name, fn):
                try:
                    fn()
                except Exception as e:  # one setting failing mustn't stop the others
                    errors[name] = str(e)

            stages = [int(v) for v in want.get("stages") or []][: c["stages"]]
            if stages and c["presets"] == "list":
                active = min(max(int(want.get("active") or 1), 1), len(stages))
                attempt("stages", lambda: mouse.set_sensitivity(",".join(map(str, stages)), active - 1))
            elif stages and c["presets"] == "pair":
                attempt("stages", lambda: mouse.set_sensitivity1(stages[0]))
                if len(stages) > 1:
                    attempt("stages", lambda: mouse.set_sensitivity2(stages[1]))
            if want.get("poll") in c["poll"]:
                attempt("poll", lambda: mouse.set_polling_rate(int(want["poll"])))
            if want.get("sleep") is not None and c["sleep"]:
                attempt("sleep", lambda: mouse.set_sleep_timer(int(want["sleep"])))
            return {"ok": not errors, "errors": errors}
    finally:
        mouse.close()
    raise SystemExit("unknown command")


if __name__ == "__main__":
    try:
        print(json.dumps(main(sys.argv[1:])))
    except Exception as e:
        print(json.dumps({"error": str(e)}))
        sys.exit(1)
