# G703 Battery Monitor

Lightweight background tray app that watches your Logitech G703 Hero 2's battery
and **only shows a tray icon when battery drops below 20%** — so you can glance
at the taskbar after a gaming session and know if you need to plug in.

No bloat. No G HUB required. Zero windows open.

---

## How it works

- Talks directly to the mouse via **HID++ 2.0** over the Lightspeed receiver
- Polls every **2 minutes** (configurable in `BatteryMonitorContext.cs`)
- Below **20%**: draws the percentage into a battery-shaped tray icon + fires one balloon notification
- Above 20%: icon disappears entirely
- Right-click the icon → **"Check now"** to force an immediate poll

---

## Requirements

- Windows 10/11 x64
- .NET 9 Runtime (or use the self-contained publish below)
- **`hidapi.dll`** — see below

---

## Getting hidapi.dll

The app P/Invokes into `hidapi.dll` (the libusb project).
The easiest source is from the existing **LGSTrayBattery** release (same SHA256 as used there):

1. Download the latest release zip from:
   https://github.com/strain08/LGSTrayEx/releases
2. Extract `hidapi.dll` from the zip
3. Place it next to `G703BatteryMonitor.exe`

Alternatively build from source: https://github.com/libusb/hidapi

---

## Build

```bash
# Standard build (requires .NET 9 installed on target machine)
dotnet build -c Release

# Single-file publish — just the .exe + hidapi.dll needed
dotnet publish -c Release -r win-x64 --self-contained false
# Output: bin\Release\net9.0-windows\win-x64\publish\G703BatteryMonitor.exe
```

---

## Run at Windows startup

1. Press `Win+R` → type `shell:startup` → Enter
2. Create a shortcut to `G703BatteryMonitor.exe` in that folder

The app will start silently with Windows and sit invisible in the background
until your mouse battery goes low.

---

## Configuration

All constants are at the top of `BatteryMonitorContext.cs`:

| Constant          | Default     | Meaning                                      |
|-------------------|-------------|----------------------------------------------|
| `PollIntervalMs`  | 120,000 ms  | How often to check battery (2 min)           |
| `LowThreshold`    | 20          | % below which the tray icon appears          |
| `RetryIntervalMs` | 30,000 ms   | Retry interval when mouse is not found       |

---

## Protocol notes

The G703 Hero uses **HID++ 2.0** via the Lightspeed receiver (USB VID `0x046D`, PID `0xC539`).
The app probes three battery features in order of preference:

| Feature ID | Name                  | Notes                              |
|------------|-----------------------|------------------------------------|
| `0x1004`   | Unified Battery       | Newest; returns % directly         |
| `0x1001`   | Battery Voltage       | G703 Hero likely uses this         |
| `0x1000`   | Battery Status        | Older mice; returns % directly     |

For `0x1001` (voltage), the app converts mV → % using a standard 3.7V LiPo
discharge curve (same as LGSTrayBattery's native mode). This may differ slightly
from what G HUB shows (G HUB uses a device-specific lookup table).

---

## Troubleshooting

**Icon never appears / "receiver not found"**
- Make sure the mouse is on and the Lightspeed receiver is plugged in
- Try running as Administrator once to check for permission issues
- If using a Unifying receiver (older), add PID `0xC52B` is already included

**Percentage seems off**
- The voltage→% conversion is approximate. If you need G HUB accuracy,
  the app can be adapted to tap into G HUB's websocket instead
  (see LGSTrayBattery_GHUB for that approach)

**Mouse goes to sleep between polls**
- The app handles this gracefully — a -1 read just keeps the last known state
  and retries on the next tick
