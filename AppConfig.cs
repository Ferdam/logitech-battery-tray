using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace G703BatteryMonitor;

public class IconThreshold
{
    public int MaxPercent { get; set; }
    public string Icon { get; set; } = "";
}

/// <summary>
/// User-editable settings loaded from <c>config.json</c> next to the executable.
/// If the file is missing it is created with defaults on first launch.
/// </summary>
public class AppConfig
{
    public int PollIntervalMs  { get; set; } = 2 * 60 * 1000;
    public int RetryIntervalMs { get; set; } = 30 * 1000;
    public string IconsFolder  { get; set; } = "icons";

    /// <summary>
    /// Ordered list of (max-percent, icon) pairs. The first entry whose
    /// MaxPercent ≥ current battery wins.
    /// Defaults match: ≤20 → 20, ≤59 → 60, ≤89 → 90, else → 100.
    /// </summary>
    public List<IconThreshold> IconThresholds { get; set; } = new()
    {
        new IconThreshold { MaxPercent = 20,  Icon = "battery_20.ico"  },
        new IconThreshold { MaxPercent = 59,  Icon = "battery_60.ico"  },
        new IconThreshold { MaxPercent = 89,  Icon = "battery_90.ico"  },
        new IconThreshold { MaxPercent = 100, Icon = "battery_100.ico" },
    };

    [JsonIgnore]
    public string BaseIconsPath => Path.IsPathRooted(IconsFolder)
        ? IconsFolder
        : Path.Combine(AppContext.BaseDirectory, IconsFolder);

    public string ResolveIconFileName(int percent)
    {
        var sorted = IconThresholds.OrderBy(t => t.MaxPercent).ToList();
        var match = sorted.FirstOrDefault(t => percent <= t.MaxPercent)
                    ?? sorted.LastOrDefault()
                    ?? new IconThreshold { Icon = "battery_100.ico" };
        return match.Icon;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static AppConfig Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "config.json");

        if (!File.Exists(path))
        {
            var defaults = new AppConfig();
            try { File.WriteAllText(path, JsonSerializer.Serialize(defaults, JsonOpts)); }
            catch { /* read-only install dir — fine, just use defaults */ }
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts);
            return cfg ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }
}
