"""Generates src/RigControl.Core/Razer/razer-mice.json from openrazer's sources.

For every Razer mouse openrazer supports, records which command variant and transaction id it uses for each
setting (DPI, DPI stages, polling rate, sleep timeout, low-battery threshold, battery, charging), plus its
name and maximum DPI. Devices that need special handling in openrazer (not a plain command) get no entry for
that setting, so RigControl never guesses.

Usage:  py -3 tools/gen-razer-devices.py [openrazer-commit]
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


driver = fetch('driver/razermouse_driver.c')
header = fetch('driver/razermouse_driver.h')
daemon = fetch('daemon/openrazer_daemon/hardware/mouse.py')

pids = {m.group(1): int(m.group(2), 16)
        for m in re.finditer(r'#define\s+USB_DEVICE_ID_RAZER_(\w+)\s+0x([0-9A-Fa-f]{4})', header)}

# setting -> sysfs handler in the driver
HANDLERS = {
    'dpi_read': 'razer_attr_read_dpi',
    'dpi_write': 'razer_attr_write_dpi',
    'stages_read': 'razer_attr_read_dpi_stages',
    'stages_write': 'razer_attr_write_dpi_stages',
    'poll_read': 'razer_attr_read_poll_rate',
    'poll_write': 'razer_attr_write_poll_rate',
    'idle_read': 'razer_attr_read_device_idle_time',
    'idle_write': 'razer_attr_write_device_idle_time',
    'lowbat_read': 'razer_attr_read_charge_low_threshold',
    'lowbat_write': 'razer_attr_write_charge_low_threshold',
    'battery': 'razer_attr_read_charge_level',
    'charging': 'razer_attr_read_charge_status',
}


def body(name):
    m = re.search(r'^static ssize_t ' + name + r'\(.*?^\}', driver, re.S | re.M)
    if not m:
        raise SystemExit(f'handler {name} not found - openrazer changed; update the generator')
    return m.group(0)


def parse(fn):
    """{pid name or 'default': (builder, transaction id)} for one handler's switch(usb_pid).

    A group is a run of `case X:` labels followed by statements; its first `request = razer_chroma_misc_*()`
    and first `transaction_id.id = 0x..` are what openrazer sends for those devices.
    """
    groups, labels, stmts = [], [], []
    outside = []  # lines not under any case label: where a shared builder lives
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
    # Pattern 2: the request is built once outside the switch; the switch only picks the transaction id.
    shared = re.search(r'request = razer_chroma_misc_(\w+)\(([^,)]*)', '\n'.join(outside))
    out, varstore = {}, set()
    for labels, stmts in groups:
        text = '\n'.join(stmts)
        if 'varstore = VARSTORE' in text:
            varstore.update(labels)
        b = re.search(r'request = razer_chroma_misc_(\w+)\(([^,)]*)', text) or shared
        t = re.search(r'transaction_id\.id = (0x[0-9A-Fa-f]+)', text)
        if b and t:
            for l in labels:
                store = b.group(2).strip() if b.group(2).strip() in ('VARSTORE', 'NOSTORE') else None
                out.setdefault(l, (b.group(1), t.group(1).lower(), l in varstore or store == 'VARSTORE'))
    return out

features = {k: parse(v) for k, v in HANDLERS.items()}

# Names and max DPI from the daemon's device classes.
classes = []
for m in re.finditer(r'^class (Razer\w+)\(__RazerDevice\):(.*?)(?=^class |\Z)', daemon, re.S | re.M):
    cls, text = m.group(1), m.group(2)
    pid = re.search(r'USB_PID\s*=\s*0x([0-9A-Fa-f]+)', text)
    if not pid:
        continue
    dpi = re.search(r'DPI_MAX\s*=\s*(\d+)', text)
    rates = re.search(r'POLL_RATES\s*=\s*\[([^\]]*)\]', text)
    doc = re.search(r'"""\s*Class for the (.*?)\s*"""', text, re.S)
    classes.append({
        'pid': int(pid.group(1), 16),
        'name': (doc.group(1).strip() if doc else cls).replace('\n', ' '),
        'dpiMax': int(dpi.group(1)) if dpi else None,
        'pollRates': [int(x) for x in re.findall(r'\d+', rates.group(1))] if rates else None,
    })
by_pid = {c['pid']: c for c in classes}

devices = {}
for name, pid in sorted(pids.items(), key=lambda kv: kv[1]):
    entry = {}
    for feat, table in features.items():
        hit = table.get(name)
        # 8 kHz (HyperPolling) writes need a second, per-device command: read-only until that's mapped too.
        if feat == 'poll_write' and hit and hit[0] == 'set_polling_rate2':
            continue
        if hit:
            entry[feat] = {'cmd': hit[0], 'tid': hit[1]}
            if hit[2]:
                entry[feat]['varstore'] = True
    if not entry:
        continue
    info = by_pid.get(pid, {})
    devices[f'{pid:04X}'] = {
        'name': info.get('name') or name.replace('_', ' ').title(),
        'dpiMax': info.get('dpiMax'),
        'pollRates': info.get('pollRates'),
        **entry,
    }

out = Path(__file__).resolve().parent.parent / 'src' / 'RigControl.Core' / 'Razer' / 'razer-mice.json'
out.parent.mkdir(parents=True, exist_ok=True)
out.write_text(json.dumps({
    'source': f'openrazer ({ref}): https://github.com/openrazer/openrazer - generated by tools/gen-razer-devices.py',
    'vendorId': '1532',
    'devices': devices,
}, indent=1, ensure_ascii=False) + '\n', encoding='utf-8')
print(f'{len(devices)} devices -> {out}')
