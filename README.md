# G703 Battery Monitor

Lightweight background tray app that watches your Logitech G703 Hero 2's battery
and shows a color-coded battery icon in the system tray. Hover for the exact %,
right-click to hide or quit.

No bloat. No G HUB required. Zero windows open.

---

## How it works

- Talks directly to the mouse via **HID++ 2.0** over the Lightspeed receiver
- Polls every **2 minutes** by default (configurable, see below)
- The tray icon swaps automatically based on battery level — defaults:
  - `battery_20.ico`  — ≤ 20 %
  - `battery_60.ico`  — 21–59 %
  - `battery_90.ico`  — 60–89 %
  - `battery_100.ico` — ≥ 90 %
- Hover the icon → tooltip shows current battery percent
- Right-click context menu:
  - **Hide** — removes the icon from the tray for this session
  - **Exit** — terminates the process

---

## Requirements

- Windows 10/11 x64
- .NET 9 Runtime (or use the self-contained publish below)

`hidapi.dll` and the icon set are now **embedded into the .exe** — no loose
files needed alongside it. (See *Customizing icons* below if you want to
override the embedded ones.)

---

## Build

```bash
# Standard build (requires .NET 9 installed on target machine)
dotnet build G703BatteryMonitor.csproj -c Release

# Single-file publish — produces a single self-contained .exe with
# hidapi.dll + icons all bundled inside
dotnet publish G703BatteryMonitor.csproj -c Release -r win-x64 --self-contained false
# Output: bin\Release\net9.0-windows\win-x64\publish\G703BatteryMonitor.exe
```

---

## Run at Windows startup

1. Press `Win+R` → type `shell:startup` → Enter
2. Create a shortcut to `G703BatteryMonitor.exe` in that folder

The app starts silently with Windows and sits in the tray showing the current
battery level.

---

## Configuration

On first launch, the app writes a `config.json` next to the executable with the
defaults shown below. Edit it, restart the app, and your changes take effect.

```json
{
  "PollIntervalMs": 120000,
  "RetryIntervalMs": 30000,
  "IconsFolder": "icons",
  "IconThresholds": [
    { "MaxPercent": 20,  "Icon": "battery_20.ico"  },
    { "MaxPercent": 59,  "Icon": "battery_60.ico"  },
    { "MaxPercent": 89,  "Icon": "battery_90.ico"  },
    { "MaxPercent": 100, "Icon": "battery_100.ico" }
  ]
}
```

| Field             | Meaning                                                       |
|-------------------|---------------------------------------------------------------|
| `PollIntervalMs`  | How often to poll the mouse (ms). Default: 2 min.             |
| `RetryIntervalMs` | Faster retry interval used when the receiver isn't found yet. |
| `IconsFolder`     | Folder (relative to .exe or absolute) used for icon overrides.|
| `IconThresholds`  | Ordered list — first entry whose `MaxPercent` ≥ battery wins. |

You can add, remove, or reorder threshold entries as you like — for example, a
stricter low-battery indicator:

```json
"IconThresholds": [
  { "MaxPercent": 10,  "Icon": "battery_20.ico"  },
  { "MaxPercent": 100, "Icon": "battery_100.ico" }
]
```

### Customizing icons

The four default icons are embedded in the .exe. To override one without
rebuilding, drop a same-named `.ico` file into `<exe-dir>\icons\`:

```
G703BatteryMonitor.exe
icons\
  battery_60.ico   ← your custom file, takes precedence over the embedded copy
```

Icon filenames are matched **case-insensitively**. You can also point
`IconsFolder` at any absolute path.

---

## Protocol notes

The G703 Hero uses **HID++ 2.0** via the Lightspeed receiver (USB VID `0x046D`,
PID `0xC539`). The app probes three battery features in order of preference:

| Feature ID | Name                  | Notes                              |
|------------|-----------------------|------------------------------------|
| `0x1004`   | Unified Battery       | Newest; returns % directly         |
| `0x1001`   | Battery Voltage       | G703 Hero typically uses this      |
| `0x1000`   | Battery Status        | Older mice; returns % directly     |

Requests/responses travel over the receiver's HID interrupt pipe (`hid_write` /
`hid_read_timeout`). The reader filters out unrelated notifications (button
clicks, wheel events, etc.) by matching report ID, device index, feature index,
and software-id tag.

For `0x1001` (voltage), the app converts mV → % using a standard 3.7 V LiPo
discharge curve (same as LGSTrayBattery's native mode). This may differ
slightly from G HUB, which uses a device-specific lookup table.

Receiver PIDs probed: `0xC539` (Lightspeed), `0xC52B` (Unifying), `0xC547`
(Bolt).

---

## Troubleshooting

**Icon doesn't appear at all**
- Check Task Manager — if `G703BatteryMonitor.exe` is running but invisible,
  expand the tray overflow ("^" in the taskbar) and drag the icon out.

**Tooltip says "receiver not found"**
- Make sure the mouse is on and the Lightspeed receiver is plugged in.
- Try running as Administrator once to rule out permission issues.

**Tooltip says "mouse asleep or off"**
- The mouse went idle between polls. The next successful poll will update.
- If it persists across many polls while the mouse is clearly active, the
  protocol probe may be falling through all three feature IDs — open an
  issue with your mouse model.

**Percentage seems off**
- The voltage→% conversion is approximate. G HUB uses a per-device lookup
  table, so its numbers will differ by a few percent.
