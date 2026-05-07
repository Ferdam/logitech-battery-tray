using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace G703BatteryMonitor;

/// <summary>
/// Background application context. No main window — lives entirely in the system tray.
///
/// Behaviour:
///   • Tray icon is shown on start by default.
///   • Polls battery on the configured interval and swaps the tray icon based on
///     the thresholds defined in config.json (next to the .exe).
///   • Tooltip always reflects the latest battery reading.
///   • Right-click context menu: "Hide", "Exit".
/// </summary>
public class BatteryMonitorContext : ApplicationContext
{
    private readonly AppConfig    _config;
    private readonly NotifyIcon   _trayIcon;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly HidPpBattery _battery;
    private          int          _lastPercent = -1;
    private          bool         _deviceFound;
    private          Icon?        _currentIcon;
    private          string?      _currentIconKey;

    private static readonly Assembly OwnAssembly = typeof(BatteryMonitorContext).Assembly;
    private const string EmbeddedIconPrefix = "G703BatteryMonitor.icons.";

    // Case-insensitive lookup table: lower-cased "battery_100.ico" → actual manifest name
    // (handles MSBuild flattening differences and any user-side casing drift).
    private static readonly Dictionary<string, string> EmbeddedIconIndex = BuildEmbeddedIconIndex();

    private static Dictionary<string, string> BuildEmbeddedIconIndex()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in OwnAssembly.GetManifestResourceNames())
        {
            if (!name.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) continue;
            // Key by the bare filename (everything after the last '.' before ".ico")
            int extDot = name.LastIndexOf('.');
            int nameDot = name.LastIndexOf('.', extDot - 1);
            string fileName = nameDot >= 0 ? name[(nameDot + 1)..] : name;
            dict[fileName] = name;
        }
        return dict;
    }

    public BatteryMonitorContext()
    {
        _config  = AppConfig.Load();
        _battery = new HidPpBattery();

        var hideItem = new ToolStripMenuItem("Hide", null, (_, _) => HideIcon());
        var exitItem = new ToolStripMenuItem("Exit", null, (_, _) => Exit());
        var menu = new ContextMenuStrip();
        menu.Items.Add(hideItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        // NotifyIcon.Visible cannot be set to true until Icon is non-null —
        // setting it earlier throws inside the ctor and the tray never appears.
        _trayIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Text             = "G703 Battery — initializing…",
            Visible          = false,
        };

        // Pick the highest-threshold icon as the placeholder until the first poll.
        ApplyIconForPercent(100);
        _trayIcon.Visible = true;

        _timer = new System.Windows.Forms.Timer { Interval = _config.PollIntervalMs };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();

        Poll();
    }

    private void Poll()
    {
        if (!_deviceFound)
        {
            _deviceFound = _battery.TryOpen();
            if (!_deviceFound)
            {
                UpdateTrayText("G703 — receiver not found");
                _timer.Interval = _config.RetryIntervalMs;
                return;
            }
            _timer.Interval = _config.PollIntervalMs;
        }

        int pct = _battery.ReadBatteryPercent();

        if (pct < 0)
        {
            UpdateTrayText("G703 — mouse asleep or off");
            return;
        }

        _lastPercent = pct;
        UpdateTrayText($"G703 Battery: {pct}%");
        ApplyIconForPercent(pct);
    }

    private void ApplyIconForPercent(int percent)
    {
        string fileName = _config.ResolveIconFileName(percent);
        if (fileName == _currentIconKey) return;

        var icon = LoadIcon(fileName);
        if (icon == null) return;

        SetIcon(icon);
        _currentIconKey = fileName;
    }

    /// <summary>
    /// Disk override first (so users can drop a custom .ico into <exe-dir>\icons\),
    /// embedded resource second.
    /// </summary>
    private Icon? LoadIcon(string fileName)
    {
        try
        {
            string diskPath = Path.Combine(_config.BaseIconsPath, fileName);
            if (File.Exists(diskPath))
                return new Icon(diskPath);
        }
        catch { /* fall through to embedded */ }

        try
        {
            // Try the exact-prefixed name first, then case-insensitive fallback.
            string? resourceName = EmbeddedIconIndex.TryGetValue(fileName, out var match)
                ? match
                : EmbeddedIconPrefix + fileName;
            using var stream = OwnAssembly.GetManifestResourceStream(resourceName);
            return stream == null ? null : new Icon(stream);
        }
        catch
        {
            return null;
        }
    }

    private void UpdateTrayText(string text)
    {
        _trayIcon.Text = text.Length > 63 ? text[..63] : text;
    }

    private void SetIcon(Icon? newIcon)
    {
        var old = _currentIcon;
        _trayIcon.Icon = newIcon;
        _currentIcon   = newIcon;
        old?.Dispose();
    }

    private void HideIcon()
    {
        _trayIcon.Visible = false;
    }

    private void Exit()
    {
        _timer.Stop();
        _trayIcon.Visible = false;
        _battery.Dispose();
        _currentIcon?.Dispose();
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _trayIcon.Dispose();
            _battery.Dispose();
            _currentIcon?.Dispose();
        }
        base.Dispose(disposing);
    }
}
