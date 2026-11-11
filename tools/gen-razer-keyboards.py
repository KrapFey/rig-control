"""Generates src/RigControl.Core/Razer/razer-keyboards.json from openrazer's keyboard driver.

For every Razer keyboard openrazer supports, records which command and transaction id it uses for game mode
(the Windows-key lock, Fn+F10), polling rate, keyswitch optimisation and battery, plus its name and polling
rates. A setting a keyboard handles specially in openrazer (not one plain command) gets no entry, so RigControl
never guesses.

Usage:  python -I tools/gen-razer-keyboards.py [openrazer-commit]
Source: https://github.com/openrazer/openrazer (GPL-2.0). This extracts facts about the protocol, not code.
"""
import json
import re
import sys
import urllib.request
from pathlib import Path

ref = sys.argv[1] if len(sys.argv) > 1 else 'master'
base = f'https://raw.githubusercontent.com/openrazer/openrazer/{ref}/'


def fetch(path):
    with urllib.request.urlopen(base + path) as r:
        return r.read().decode('utf-8')


driver = fetch('driver/razerkbd_driver.c')
header = fetch('driver/razerkbd_driver.h')
daemon = fetch('daemon/openrazer_daemon/hardware/keyboards.py')

pids = {m.group(1): int(m.group(2), 16)
        for m in re.finditer(r'#define\s+USB_DEVICE_ID_RAZER_(\w+)\s+0x([0-9A-Fa-f]{4})', header)}

HANDLERS = {
    'game_read': 'razer_attr_read_game_led_state',
    'game_write': 'razer_attr_write_game_led_state',
    'poll_read': 'razer_attr_read_poll_rate',
    'poll_write': 'razer_attr_write_poll_rate',
    'keyswitch_read': 'razer_attr_read_keyswitch_optimization',
    'keyswitch_write': 'razer_attr_write_keyswitch_optimization',
    'battery': 'razer_attr_read_charge_level',
    'charging': 'razer_attr_read_charge_status',
}


def body(name):
    m = re.search(r'^static ssize_t ' + name + r'\(.*?^\}', driver, re.S | re.M)
    if not m:
        raise SystemExit(f'handler {name} not found - openrazer changed; update the generator')
    return m.group(0)


def parse(fn):
    """{pid name or 'default': (builder, transaction id, storage)} for one handler's switch(usb_pid)."""
    groups, labels, stmts, outside = [], [], [], []
    for line in body(fn).splitlines():
        s = line.strip()
        m = re.match(r'case USB_DEVICE_ID_RAZER_(\w+):', s)
        if m or s.startswith('default:'):
            if stmts:
                groups.append((labels, stmts))
                labels, stmts = [], []
            labels.append(m.group(1) if m else 'default')
        elif labels and s:
            stmts.append(s)
            if s.startswith('break;'):
                groups.append((labels, stmts))
                labels, stmts = [], []
        elif s:
            outside.append(s)
    if labels:
        groups.append((labels, stmts))
    # The request may be built once before the switch, which then only picks the transaction id.
    shared = re.search(r'request = razer_chroma_(\w+)\(([^)]*)\)', '\n'.join(outside))
    out = {}
    for labels, stmts in groups:
        text = '\n'.join(stmts)
        b = re.search(r'request = razer_chroma_(\w+)\(([^)]*)\)', text) or shared
        t = re.search(r'transaction_id\.id = (0x[0-9A-Fa-f]+)', text)
        if b and t:
            args = [a.strip() for a in b.group(2).split(',') if a.strip()]
            storage = args[0] if args and args[0] in ('VARSTORE', 'NOSTORE') else None
            for l in labels:
                out.setdefault(l, (b.group(1), t.group(1).lower(), storage))
    return out


features = {k: parse(v) for k, v in HANDLERS.items()}

classes = {}
for m in re.finditer(r'^class (Razer\w+)\(_\w+\):(.*?)(?=^class |\Z)', daemon, re.S | re.M):
    cls, text = m.group(1), m.group(2)
    pid = re.search(r'USB_PID\s*=\s*0x([0-9A-Fa-f]+)', text)
    if not pid:
        continue
    rates = re.search(r'POLL_RATES\s*=\s*\[([^\]]*)\]', text)
    doc = re.search(r'"""\s*Class for the (.*?)\s*"""', text, re.S)
    classes[int(pid.group(1), 16)] = {
        'name': (doc.group(1).strip() if doc else cls).replace('\n', ' '),
        'pollRates': [int(x) for x in re.findall(r'\d+', rates.group(1))] if rates else None,
    }

# Only the command shapes RigControl implements; anything else is left out for that keyboard.
SHAPES = {
    'game_read': 'standard_get_led_state', 'game_write': 'standard_set_led_state',
    'poll_read': 'misc_get_polling_rate2', 'poll_write': 'misc_set_polling_rate2',
    'keyswitch_read': 'misc_get_keyswitch_optimization', 'keyswitch_write': 'misc_set_keyswitch_optimization_command1',
    'battery': 'misc_get_battery_level', 'charging': 'misc_get_charging_status',
}

devices = {}
for name, pid in sorted(pids.items(), key=lambda kv: kv[1]):
    if pid not in classes:
        continue  # not a keyboard the daemon knows (laptops' other interfaces, …)
    entry = {}
    for feat, table in features.items():
        hit = table.get(name)
        if hit and hit[0] == SHAPES[feat]:
            entry[feat] = {'tid': hit[1]}
            if hit[2] == 'VARSTORE':
                entry[feat]['varstore'] = True
    if not entry:
        continue
    devices[f'{pid:04X}'] = {'name': classes[pid]['name'], 'pollRates': classes[pid]['pollRates'], **entry}

out = Path(__file__).resolve().parent.parent / 'src' / 'RigControl.Core' / 'Razer' / 'razer-keyboards.json'
out.write_text(json.dumps({
    'source': f'openrazer ({ref}): https://github.com/openrazer/openrazer - generated by tools/gen-razer-keyboards.py',
    'vendorId': '1532',
    'devices': devices,
}, indent=1, ensure_ascii=False) + '\n', encoding='utf-8')
print(f'{len(devices)} keyboards -> {out}')
