using System;
using System.Diagnostics;
using System.IO;

namespace KrakenEliteScreenManager.Services;

/// <summary>
/// Installs and controls the per-user systemd service that runs the dashboard
/// daemon, so the chosen mode is applied at login and kept running without the GUI.
/// </summary>
public static class SystemdManager
{
    public const string Unit = "kraken-elite-screen-manager.service";

    private static string UnitPath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "systemd", "user", Unit);

    /// <summary>Write the unit file (ExecStart points at this very binary) and reload systemd.</summary>
    public static void InstallUnit()
    {
        var exe = Environment.ProcessPath ?? "/usr/bin/dotnet";
        string execStart = Path.GetFileName(exe).StartsWith("dotnet")
            ? $"{exe} {Path.Combine(AppContext.BaseDirectory, "KrakenEliteScreenManager.dll")} service"
            : $"{exe} service";

        var unit = $"""
            [Unit]
            Description=Kraken LCD dashboard
            After=graphical-session.target

            [Service]
            ExecStart={execStart}
            Restart=always
            RestartSec=5

            [Install]
            WantedBy=default.target
            """;

        Directory.CreateDirectory(Path.GetDirectoryName(UnitPath)!);
        File.WriteAllText(UnitPath, unit);
        Systemctl("daemon-reload");
    }

    /// <summary>Install + enable (start at login) + (re)start now, so the new config takes effect.</summary>
    public static void ApplyAndRestart()
    {
        InstallUnit();
        Systemctl("enable", Unit);
        Systemctl("restart", Unit);
    }

    public static void Stop()
    {
        Systemctl("stop", Unit);
        Systemctl("disable", Unit);
    }

    public static bool IsActive() => Systemctl("is-active", Unit).stdout.Trim() == "active";

    private static (int code, string stdout) Systemctl(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("systemctl")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--user");
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi)!;
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            return (p.ExitCode, outp);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
