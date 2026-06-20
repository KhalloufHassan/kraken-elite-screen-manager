using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace KrakenEliteScreenManager.Services;

/// <summary>
/// Tiny local web server backing the dashboard HTML: serves the page at "/" and
/// live temps at "/data.json" (same-origin, no CORS fuss).
/// </summary>
public sealed class SensorServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly TemperatureService _temps;
    private readonly SystemStats _stats = new();
    private readonly string _htmlPath;
    private Thread? _thread;
    private volatile bool _running;

    public string Url { get; }

    public SensorServer(string htmlPath, TemperatureService temps, int port = 9234)
    {
        _htmlPath = htmlPath;
        _temps = temps;
        Url = $"http://localhost:{port}/";
        _listener.Prefixes.Add(Url);
    }

    public void Start()
    {
        _listener.Start();
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "SensorServer" };
        _thread.Start();
    }

    private void Loop()
    {
        while (_running)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
            catch { break; }
            try { Handle(ctx); } catch { }
            finally { try { ctx.Response.Close(); } catch { } }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        if (path == "/data.json")
        {
            var net = _stats.Network();
            var payload = JsonSerializer.Serialize(new
            {
                coolant = _temps.Read(TempSource.Coolant),
                cpu = _temps.Read(TempSource.Cpu),
                gpu = _stats.GpuTemp(),        // GPU temp (nvidia-smi or amdgpu)
                cpuLoad = _stats.CpuLoad(),     // % over the poll interval
                gpuLoad = _stats.GpuLoad(),     // GPU utilization %
                ram = _stats.RamLoad(),         // RAM in use %
                gpuMem = _stats.GpuMem(),       // GPU VRAM in use % (nvidia)
                netRx = net.rxKbps,             // network down, KB/s
                netTx = net.txKbps,             // network up, KB/s
            });
            ctx.Response.Headers["Cache-Control"] = "no-store";
            Write(ctx, "application/json", Encoding.UTF8.GetBytes(payload));
        }
        else
        {
            var html = File.Exists(_htmlPath)
                ? File.ReadAllBytes(_htmlPath)
                : Encoding.UTF8.GetBytes("<h1 style='color:red'>dashboard.html not found</h1>");
            Write(ctx, "text/html; charset=utf-8", html);
        }
    }

    private static void Write(HttpListenerContext ctx, string contentType, byte[] body)
    {
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = body.Length;
        ctx.Response.OutputStream.Write(body, 0, body.Length);
    }

    public void Dispose()
    {
        _running = false;
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
    }
}
