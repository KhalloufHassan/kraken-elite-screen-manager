using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace KrakenEliteScreenManager.Services;

/// <summary>
/// The headless daemon (run via `KrakenEliteScreenManager service`, started by systemd).
/// Applies the configured mode and keeps it up:
///   GifLoop   - upload the GIF once; firmware loops it (no overlay).
///   Dashboard - render dashboard.html with headless Chromium and push each frame
///               double-buffered (flicker-free), ~4 fps.
///   Coolant   - the built-in liquid-temperature screen.
/// Restores the (upright) coolant screen on clean stop; self-recovers from USB hiccups.
/// </summary>
public static class ServiceRunner
{
    public static async Task<int> RunAsync()
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; cts.Cancel(); });

        var temps = new TemperatureService();
        Log("service starting");

        KrakenLcdDriver? driver = null;
        while (!cts.IsCancellationRequested)
        {
            try
            {
                var cfg = DashboardConfig.Load();
                driver?.Dispose();
                driver = new KrakenLcdDriver(Log);
                driver.Open();

                bool hasGif = File.Exists(DashboardConfig.GifFile);

                if (cfg.Mode == DashboardMode.GifLoop && hasGif)
                {
                    driver.SetBrightness(cfg.Brightness, orientation: 0);
                    var gif = GifPreparer.MakeLoop(await File.ReadAllBytesAsync(DashboardConfig.GifFile, cts.Token), cfg.Rotation);
                    driver.PushImage(gif);
                    Log("gif-loop active");
                    await Task.Delay(Timeout.Infinite, cts.Token);
                }
                else if (cfg.Mode == DashboardMode.Dashboard)
                {
                    driver.SetBrightness(cfg.Brightness, orientation: 0); // content pre-rotated in software
                    await RunDashboardAsync(driver, temps, cfg, cts.Token);
                }
                else
                {
                    if (cfg.Mode == DashboardMode.GifLoop) Log("no bg.gif found — showing coolant");
                    driver.SetBrightness(cfg.Brightness, orientation: cfg.Rotation / 90); // upright stock screen
                    driver.ShowLiquid();
                    Log("coolant active");
                    await Task.Delay(Timeout.Infinite, cts.Token);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log($"error: {ex.Message} — resetting device, retry in 5s");
                KrakenLcdDriver.ResetDevice(Log);
                driver?.Dispose();
                driver = null;
                try { await Task.Delay(TimeSpan.FromSeconds(5), cts.Token); } catch { break; }
            }
        }

        // Clean stop: restore panel orientation, then the upright stock coolant screen.
        try
        {
            if (driver is not null)
            {
                var cfg = DashboardConfig.Load();
                driver.SetBrightness(cfg.Brightness, orientation: cfg.Rotation / 90);
                driver.ShowLiquid();
            }
        }
        catch { /* best effort */ }
        driver?.Dispose();
        Log("service stopping");
        return 0;
    }

    private static async Task RunDashboardAsync(KrakenLcdDriver driver, TemperatureService temps, DashboardConfig cfg, CancellationToken ct)
    {
        var html = Path.Combine(AppContext.BaseDirectory, "assets", "dashboard.html");
        using var server = new SensorServer(html, temps);
        server.Start();
        await using var renderer = new WebRenderer(KrakenLcdDriver.Width);
        await renderer.StartAsync(server.Url);
        Log("dashboard active");

        while (!ct.IsCancellationRequested)
        {
            var png = await renderer.CaptureAsync();
            using (var img = Image.Load<Rgba32>(png))
            {
                LcdFrame.Fit(img, KrakenLcdDriver.Width, cfg.Rotation);
                driver.PushFrameDoubleBuffered(LcdFrame.ToGif(img)); // double-buffered → flicker-free
            }
            await Task.Delay(250, ct); // ~4 fps (proven stable + flicker-free)
        }
    }

    private static void Log(string m) => Console.WriteLine($"[kraken] {m}");
}
