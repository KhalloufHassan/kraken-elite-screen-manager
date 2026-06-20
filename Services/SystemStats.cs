using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace KrakenEliteScreenManager.Services;

/// <summary>
/// Live utilization with no sudo: CPU busy % from /proc/stat (delta between polls),
/// and GPU load + temperature auto-detected — NVIDIA via nvidia-smi, otherwise an
/// AMD card via /sys (gpu_busy_percent + hwmon temp). nvidia-smi is cached briefly
/// so rapid polls don't spawn a process each time.
/// </summary>
public sealed class SystemStats
{
    private enum GpuKind { None, Nvidia, Amd }

    private readonly object _gate = new();

    private long _prevIdle = -1, _prevTotal;
    private long _prevRx = -1, _prevTx;
    private DateTime _netStamp;

    private readonly GpuKind _gpu;
    private readonly string? _amdTemp;   // hwmon temp1_input (milli-°C)
    private readonly string? _amdBusy;    // device/gpu_busy_percent (0-100)

    private DateTime _nvStamp = DateTime.MinValue;
    private double? _nvTemp, _nvLoad, _nvMem;

    public SystemStats()
    {
        // Locate an AMD card's sysfs (fallback path).
        foreach (var dir in SafeDirs("/sys/class/hwmon"))
        {
            if (!string.Equals(ReadText(Path.Combine(dir, "name")), "amdgpu", StringComparison.OrdinalIgnoreCase))
                continue;
            var t = Path.Combine(dir, "temp1_input");
            var b = Path.Combine(dir, "device", "gpu_busy_percent");
            _amdTemp = File.Exists(t) ? t : null;
            _amdBusy = File.Exists(b) ? b : null;
            break;
        }

        // Prefer NVIDIA if nvidia-smi is present and responds.
        _gpu = NvidiaPresent() ? GpuKind.Nvidia
             : (_amdTemp is not null || _amdBusy is not null) ? GpuKind.Amd
             : GpuKind.None;
    }

    /// <summary>CPU busy % over the interval since the previous call (0-100); null on the first call.</summary>
    public double? CpuLoad()
    {
        try
        {
            // First line: "cpu  user nice system idle iowait irq softirq steal guest ..."
            var line = File.ReadLines("/proc/stat").FirstOrDefault();
            if (line is null) return null;
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            long total = 0, idle = 0;
            for (int i = 1; i < p.Length; i++)
            {
                if (!long.TryParse(p[i], out var v)) continue;
                total += v;
                if (i == 4 || i == 5) idle += v; // idle + iowait
            }

            lock (_gate)
            {
                if (_prevIdle < 0) { _prevIdle = idle; _prevTotal = total; return null; }
                long dTotal = total - _prevTotal, dIdle = idle - _prevIdle;
                _prevIdle = idle;
                _prevTotal = total;
                if (dTotal <= 0) return null;
                return Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100);
            }
        }
        catch { return null; }
    }

    /// <summary>RAM in use (%) from /proc/meminfo.</summary>
    public double? RamLoad()
    {
        try
        {
            long total = 0, avail = 0;
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:")) total = MemKb(line);
                else if (line.StartsWith("MemAvailable:")) { avail = MemKb(line); break; }
            }
            return total > 0 ? Math.Clamp(100.0 * (total - avail) / total, 0, 100) : null;
        }
        catch { return null; }
    }

    /// <summary>Network throughput (KB/s rx, tx) over the interval since the previous call.</summary>
    public (double rxKbps, double txKbps) Network()
    {
        try
        {
            long rx = 0, tx = 0;
            foreach (var line in File.ReadLines("/proc/net/dev"))
            {
                int c = line.IndexOf(':');
                if (c < 0) continue;
                if (line[..c].Trim() == "lo") continue;
                var n = line[(c + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (n.Length < 9) continue;
                if (long.TryParse(n[0], out var r)) rx += r;
                if (long.TryParse(n[8], out var t)) tx += t;
            }
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                if (_prevRx < 0) { _prevRx = rx; _prevTx = tx; _netStamp = now; return (0, 0); }
                double secs = (now - _netStamp).TotalSeconds;
                if (secs <= 0) secs = 1;
                double rxk = (rx - _prevRx) / secs / 1024.0, txk = (tx - _prevTx) / secs / 1024.0;
                _prevRx = rx; _prevTx = tx; _netStamp = now;
                return (Math.Max(0, rxk), Math.Max(0, txk));
            }
        }
        catch { return (0, 0); }
    }

    public double? GpuMem()
    {
        if (_gpu != GpuKind.Nvidia) return null;
        Nvidia();
        return _nvMem;
    }

    private static long MemKb(string line)
    {
        var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return p.Length >= 2 && long.TryParse(p[1], out var v) ? v : 0;
    }

    public double? GpuLoad() => _gpu switch
    {
        GpuKind.Nvidia => Nvidia().load,
        GpuKind.Amd => long.TryParse(ReadText(_amdBusy), out var b) ? b : null,
        _ => null,
    };

    public double? GpuTemp() => _gpu switch
    {
        GpuKind.Nvidia => Nvidia().temp,
        GpuKind.Amd => long.TryParse(ReadText(_amdTemp), out var m) ? m / 1000.0 : null,
        _ => null,
    };

    // --- nvidia-smi (temp + utilization in one cached call) ------------------

    private (double? temp, double? load) Nvidia()
    {
        lock (_gate)
        {
            if ((DateTime.UtcNow - _nvStamp).TotalMilliseconds >= 900)
            {
                _nvStamp = DateTime.UtcNow;
                var f = RunNvidia("--query-gpu=temperature.gpu,utilization.gpu,memory.used,memory.total --format=csv,noheader,nounits")
                    ?.Split(',', StringSplitOptions.TrimEntries) ?? Array.Empty<string>();
                _nvTemp = f.Length > 0 && double.TryParse(f[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var t) ? t : null;
                _nvLoad = f.Length > 1 && double.TryParse(f[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var u) ? u : null;
                double? mu = f.Length > 2 && double.TryParse(f[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var a) ? a : null;
                double? mt = f.Length > 3 && double.TryParse(f[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var b) ? b : null;
                _nvMem = mu.HasValue && mt is > 0 ? Math.Clamp(mu.Value / mt.Value * 100, 0, 100) : null;
            }
            return (_nvTemp, _nvLoad);
        }
    }

    private static bool NvidiaPresent() => RunNvidia("-L") is { Length: > 0 };

    /// <summary>Run nvidia-smi and return the first non-empty output line, or null if unavailable.</summary>
    private static string? RunNvidia(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var output = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(2000) || proc.ExitCode != 0) return null;
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        }
        catch { return null; }
    }

    // --- sysfs helpers -------------------------------------------------------

    private static string[] SafeDirs(string root)
    {
        try { return Directory.Exists(root) ? Directory.GetDirectories(root) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    private static string? ReadText(string? path)
    {
        try { return path is not null && File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch { return null; }
    }
}
