"""RigControl <-> liquidctl helper.

One JSON object per line on stdin, one reply per line on stdout (logs go to stderr).

  {"op": "start", "exclude": ["<hid path>", ...]}   -> {"devices": [...]}
  {"op": "status"}                                  -> {"status": {"<id>": {"channels": {...}, "temps": {...}} | {"error": "..."}}}
  {"op": "set", "items": [{"dev", "channel", "duty"}]} -> {"ok": true, "errors": {...}}
  {"op": "release", "items": [{"dev", "channel"}]}  -> {"ok": true}
  {"op": "quit"}

Only fan/pump drivers are touched; RGB-only drivers (Aura, RGB Fusion, DDR4, NVIDIA, ...) are never
opened, so lighting owned by other software is left alone. A device is only initialized when it
doesn't answer a status read, because initializing some (HUE 2) rescans fans and silences them.

SPDX-License-Identifier: GPL-3.0-or-later
"""
import json
import re
import sys
import time
import zlib

# Modules whose devices control fans or pumps.
FAN_MODULES = {
    'smart_device', 'kraken2', 'kraken3', 'commander_pro', 'commander_core', 'hydro_platinum',
    'asetek', 'asetek_pro', 'coolit', 'aquacomputer', 'asus_ryujin', 'asus_ryuo', 'lianli_uni',
    'msi', 'control_hub', 'corsair_hid_psu', 'nzxt_epsu',
}

PUMP_FLOOR = 50      # ponytail: never run a pump below this; make it a setting if someone needs lower
RELEASE_FAN = 60     # what fans/pumps are left at when RigControl lets go (most have no "auto" mode)
RELEASE_PUMP = 100
RESEND_SECONDS = 10  # re-send unchanged duties this often, in case the device reset

_CHANNEL_RULES = [
    (re.compile(r'^fan (\d+) (speed|duty)$'), lambda m: f'fan{m.group(1)}'),
    (re.compile(r'^fan (speed|duty)$'), lambda m: 'fan'),
    (re.compile(r'^pump fan (speed|duty)$'), lambda m: 'pump-fan'),
    (re.compile(r'^pump (speed|duty)$'), lambda m: 'pump'),
    (re.compile(r'^external fans? (speed|duty)$'), lambda m: 'fans'),
]


def parse_status(status):
    """liquidctl status tuples -> ({channel: {"label", "rpm", "duty"}}, {temp label: value})."""
    channels, temps = {}, {}
    for key, value, unit in status:
        k = key.strip().lower()
        for rule, name in _CHANNEL_RULES:
            m = rule.match(k)
            if m:
                ch = channels.setdefault(name(m), {'label': key.rsplit(' ', 1)[0], 'rpm': None, 'duty': None})
                if m.group(m.lastindex) == 'speed' and unit == 'rpm':
                    ch['rpm'] = value
                elif m.group(m.lastindex) == 'duty':
                    ch['duty'] = value
                break
        else:
            if unit == '°C' and isinstance(value, (int, float)):
                temps[key] = value
    return channels, temps


def device_id(dev):
    serial = _serial(dev)
    return serial if serial else f'{zlib.crc32(str(dev.address).encode()) & 0xFFFFFFFF:08x}'


def fancontrol_id(dev):
    """The id FanControl.Liquidctl gives the device, so its configs can be imported."""
    key = f'{dev.description}|{_serial(dev)}|{dev.address}'
    return zlib.crc32(key.encode()) & 0x7FFFFFFF or 1


def _serial(dev):
    try:
        return dev.serial_number
    except Exception:
        return None


class Bridge:
    def __init__(self):
        self.devices = {}   # id -> driver
        self.sent = {}      # (id, channel) -> (duty, time)

    def start(self, exclude):
        import liquidctl
        excluded = {p.lower() for p in exclude}
        found = []
        for dev in liquidctl.find_liquidctl_devices():
            module = type(dev).__module__.rsplit('.', 1)[-1]
            if module not in FAN_MODULES or str(dev.address).lower() in excluded:
                continue
            if getattr(dev, '_speed_channels', None) == {}:  # e.g. NZXT 2023 RGB controller: lighting only
                continue
            try:
                dev.connect()
                try:
                    status = dev.get_status()
                except Exception:
                    dev.initialize()
                    status = dev.get_status()
                channels, temps = parse_status(status)
                if not channels:
                    dev.disconnect()
                    continue
                did = device_id(dev)
                self.devices[did] = dev
                found.append({
                    'id': did, 'module': module, 'description': dev.description, 'serial': _serial(dev),
                    'address': str(dev.address), 'fancontrol_id': fancontrol_id(dev),
                    'channels': channels, 'temps': temps,
                })
            except Exception as e:
                log(f'skipping {dev.description}: {e}')
                try:
                    dev.disconnect()
                except Exception:
                    pass
        return {'devices': found}

    def status(self):
        out = {}
        for did, dev in self.devices.items():
            try:
                channels, temps = parse_status(dev.get_status())
                out[did] = {'channels': channels, 'temps': temps}
            except Exception as e:
                out[did] = {'error': str(e)}
        return {'status': out}

    def set(self, items, force=False):
        errors = {}
        now = time.monotonic()
        by_dev = {}
        for it in items:
            duty = max(0, min(100, int(round(it['duty']))))
            if 'pump' in it['channel']:
                duty = max(duty, PUMP_FLOOR)
            last = self.sent.get((it['dev'], it['channel']))
            if not force and last and last[0] == duty and now - last[1] < RESEND_SECONDS:
                continue
            by_dev.setdefault(it['dev'], []).append((it['channel'], duty))
        for did, chans in by_dev.items():
            dev = self.devices.get(did)
            if dev is None:
                errors[did] = 'unknown device'
                continue
            try:
                if type(dev).__name__ == 'SmartDevice2' and all(re.fullmatch(r'fan[123]', c) for c, _ in chans):
                    # HUE 2 takes about one duty command per status cycle; liquidctl sends one per channel,
                    # so batch them into a single masked command (same as RigControl's native driver).
                    msg = [0x62, 0x01, 0, 0, 0, 0]
                    for c, d in chans:
                        i = int(c[3]) - 1
                        msg[2] |= 1 << i
                        msg[3 + i] = d
                    dev._write(msg)
                else:
                    for c, d in chans:
                        dev.set_fixed_speed(c, d)
                        time.sleep(0.05)
                for c, d in chans:
                    self.sent[(did, c)] = (d, now)
            except Exception as e:
                errors[did] = str(e)
        return {'ok': not errors, 'errors': errors}

    def release(self, items):
        return self.set([dict(it, duty=RELEASE_PUMP if 'pump' in it['channel'] else RELEASE_FAN) for it in items], force=True)

    def quit(self):
        for dev in self.devices.values():
            try:
                dev.disconnect()
            except Exception:
                pass


def log(msg):
    print(msg, file=sys.stderr, flush=True)


def main():
    bridge = Bridge()
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            req = json.loads(line)
            op = req.get('op')
            if op == 'start':
                reply = bridge.start(req.get('exclude', []))
            elif op == 'status':
                reply = bridge.status()
            elif op == 'set':
                reply = bridge.set(req['items'])
            elif op == 'release':
                reply = bridge.release(req['items'])
            elif op == 'quit':
                bridge.quit()
                print(json.dumps({'ok': True}), flush=True)
                return
            else:
                reply = {'error': f'unknown op {op!r}'}
        except Exception as e:
            reply = {'error': f'{type(e).__name__}: {e}'}
        print(json.dumps(reply), flush=True)
    bridge.quit()


def selftest():
    status = [
        ('Fan 1 speed', 1012, 'rpm'), ('Fan 1 duty', 40, '%'), ('Fan 1 control mode', 'PWM', ''),
        ('Fan 2 speed', 980, 'rpm'), ('Fan 2 duty', 40, '%'),
        ('Pump speed', 2100, 'rpm'), ('Pump duty', 70, '%'),
        ('Fan speed', 800, 'rpm'),
        ('Pump fan speed', 1500, 'rpm'),
        ('External fan duty', 55, '%'),
        ('Liquid temperature', 31.2, '°C'),
        ('Noise level', 34, 'dB'),
    ]
    ch, temps = parse_status(status)
    assert set(ch) == {'fan1', 'fan2', 'pump', 'fan', 'pump-fan', 'fans'}, ch
    assert ch['fan1'] == {'label': 'Fan 1', 'rpm': 1012, 'duty': 40}, ch['fan1']
    assert ch['pump']['rpm'] == 2100 and ch['pump']['duty'] == 70
    assert ch['fans']['duty'] == 55 and ch['fans']['rpm'] is None
    assert temps == {'Liquid temperature': 31.2}, temps

    class Fake:
        description = 'NZXT RGB & Fan Controller (3+6 channels)'
        serial_number = '0123456789ABCDEF0123456789ABCDEF'
        address = '\\\\?\\HID#VID_1E71&PID_2020#7&1a2b3c4d&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}'
    assert fancontrol_id(Fake()) == 1660399584, fancontrol_id(Fake())  # same value RigControl's C# computes

    b = Bridge()
    sent = []
    class FakeDev:
        def set_fixed_speed(self, c, d): sent.append((c, d))
    b.devices['x'] = FakeDev()
    b.set([{'dev': 'x', 'channel': 'pump', 'duty': 10}, {'dev': 'x', 'channel': 'fan1', 'duty': 33.4}])
    assert sent == [('pump', PUMP_FLOOR), ('fan1', 33)], sent
    b.set([{'dev': 'x', 'channel': 'fan1', 'duty': 33}])
    assert len(sent) == 2, 'unchanged duty is not re-sent'
    print('bridge selftest ok')


if __name__ == '__main__':
    selftest() if '--selftest' in sys.argv else main()
