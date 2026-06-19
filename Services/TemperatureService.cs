using System;
using System.IO;

namespace KrakenEliteScreenManager.Services;

public enum TempSource
{
    Coolant,
    Cpu,
}

/// <summary>
/// Reads temperatures straight from /sys/class/hwmon — no sudo required.
/// hwmon indices shuffle across reboots, so we resolve each chip by its
/// reported name once at construction.
/// </summary>
public class TemperatureService
{
    private const string HwmonRoot = "/sys/class/hwmon";

    private readonly string? _coolantInput;
    private readonly string? _cpuInput;

    public TemperatureService()
    {
        // kraken2023elite -> coolant/liquid temp (this is what NZXT shows)
        _coolantInput = FindTempInput("kraken2023elite", "temp1");
        // CPU package temp: k10temp (AMD) or coretemp (Intel)
        _cpuInput = FindTempInput("k10temp", "temp1") ?? FindTempInput("coretemp", "temp1");
        // GPU temp is auto-detected (nvidia-smi or amdgpu) via SystemStats, not hwmon.
    }

    public bool IsAvailable(TempSource source) => InputFor(source) is not null;

    /// <summary>Returns the temperature in °C, or null if the sensor is unavailable/unreadable.</summary>
    public double? Read(TempSource source)
    {
        var path = InputFor(source);
        if (path is null) return null;

        try
        {
            var raw = File.ReadAllText(path).Trim();
            // hwmon reports milli-degrees Celsius
            if (long.TryParse(raw, out var milli))
                return milli / 1000.0;
        }
        catch
        {
            // sensor vanished or unreadable — treat as unavailable
        }

        return null;
    }

    private string? InputFor(TempSource source) => source switch
    {
        TempSource.Coolant => _coolantInput,
        TempSource.Cpu => _cpuInput,
        _ => null,
    };

    private static string? FindTempInput(string chipName, string tempPrefix)
    {
        if (!Directory.Exists(HwmonRoot)) return null;

        foreach (var dir in Directory.GetDirectories(HwmonRoot))
        {
            try
            {
                var namePath = Path.Combine(dir, "name");
                if (!File.Exists(namePath)) continue;

                var name = File.ReadAllText(namePath).Trim();
                if (!string.Equals(name, chipName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var input = Path.Combine(dir, $"{tempPrefix}_input");
                if (File.Exists(input)) return input;
            }
            catch
            {
                // skip unreadable hwmon entries
            }
        }

        return null;
    }
}
