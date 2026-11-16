//! A RigControl plugin that pretends to be a mouse. It speaks the whole protocol (docs/plugins.md) without any
//! hardware, so it shows the shape of a real plugin: read a request line, answer with one line, repeat until stdin
//! closes. A real plugin would talk to its device (the `hidapi` crate, for example) where this one keeps a struct.

use serde_json::{json, Value};
use std::io::{self, BufRead, Write};

const KEY: &str = "mouse-1";
const POLL_RATES: [i64; 3] = [125, 500, 1000];

struct Mouse {
    dpi: i64,
    poll_rate: i64,
    sleep_seconds: i64,
}

fn main() {
    // stderr goes to RigControl's log (rigcontrol.log), stdout is for answers only.
    eprintln!("sample mouse plugin {} started", env!("CARGO_PKG_VERSION"));
    let mut mouse = Mouse { dpi: 1600, poll_rate: 1000, sleep_seconds: 300 };
    let mut out = io::stdout().lock();
    for line in io::stdin().lock().lines() {
        let Ok(line) = line else { break };
        if line.trim().is_empty() {
            continue;
        }
        let reply = match serde_json::from_str::<Value>(&line) {
            Ok(request) => handle(&request, &mut mouse).unwrap_or_else(|e| json!({ "error": e })),
            Err(e) => json!({ "error": format!("not JSON: {e}") }),
        };
        if writeln!(out, "{reply}").and_then(|_| out.flush()).is_err() {
            break; // RigControl went away
        }
    }
    // stdin closed: RigControl is stopping the plugin
}

fn handle(request: &Value, mouse: &mut Mouse) -> Result<Value, String> {
    match request["op"].as_str() {
        Some("hello") => Ok(json!({ "protocol": 1 })),
        Some("discover") => Ok(json!({
            "devices": [{
                "key": KEY,
                "name": "Sample mouse",
                "kind": "Mouse",
                "caps": {
                    "dpiMin": 100, "dpiMax": 16000, "dpiStep": 50,
                    "pollRates": POLL_RATES,
                    "sleepTimeout": true, "sleepMinSeconds": 60, "sleepMaxSeconds": 900,
                    "battery": true
                }
            }]
        })),
        Some("read") => {
            device(request)?;
            Ok(json!({
                "state": {
                    "dpi": mouse.dpi,
                    "pollRate": mouse.poll_rate,
                    "sleepSeconds": mouse.sleep_seconds,
                    "batteryPercent": 77,
                    "charging": false
                }
            }))
        }
        Some("apply") => {
            device(request)?;
            let settings = &request["settings"];
            // Only the settings RigControl sends are set; anything this device doesn't have is ignored.
            if let Some(dpi) = settings["dpi"].as_i64() {
                if !(100..=16000).contains(&dpi) {
                    return Err(format!("{dpi} DPI is out of range"));
                }
                mouse.dpi = dpi / 50 * 50;
            }
            if let Some(rate) = settings["pollRate"].as_i64() {
                if !POLL_RATES.contains(&rate) {
                    return Err(format!("{rate} Hz isn't a polling rate this mouse has"));
                }
                mouse.poll_rate = rate;
            }
            if let Some(seconds) = settings["sleepSeconds"].as_i64() {
                mouse.sleep_seconds = seconds.clamp(60, 900);
            }
            Ok(json!({ "ok": true }))
        }
        Some(op) => Err(format!("unknown op \"{op}\"")),
        None => Err("no op".into()),
    }
}

/// Every request about a device names it by the key `discover` gave.
fn device(request: &Value) -> Result<(), String> {
    match request["key"].as_str() {
        Some(KEY) => Ok(()),
        other => Err(format!("no device \"{}\"", other.unwrap_or(""))),
    }
}
