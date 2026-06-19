using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace KrakenEliteScreenManager.Services;

/// <summary>
/// Renders the dashboard HTML to a 640x640 frame with headless Chromium.
/// JPEG capture (fast to encode/decode) for the per-frame loop.
/// </summary>
public sealed class WebRenderer : IAsyncDisposable
{
    public int Size { get; }

    private IPlaywright? _pw;
    private IBrowser? _browser;
    private IPage? _page;

    public WebRenderer(int size = 640) => Size = size;

    public async Task StartAsync(string target)
    {
        _pw = await Playwright.CreateAsync();
        _browser = await _pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        _page = await _browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = Size, Height = Size },
            DeviceScaleFactor = 1,
        });
        await _page.GotoAsync(ResolveTarget(target), new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
    }

    public async Task<byte[]> CaptureAsync()
    {
        if (_page is null) throw new InvalidOperationException("Renderer not started.");
        return await _page.ScreenshotAsync(new PageScreenshotOptions
        {
            Type = ScreenshotType.Jpeg,
            Quality = 85,
            Clip = new Clip { X = 0, Y = 0, Width = Size, Height = Size },
        });
    }

    private static string ResolveTarget(string target)
    {
        if (target.StartsWith("http://") || target.StartsWith("https://") || target.StartsWith("file://"))
            return target;
        var full = Path.GetFullPath(target);
        if (!File.Exists(full)) throw new FileNotFoundException($"HTML file not found: {full}");
        return new Uri(full).AbsoluteUri;
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) await _browser.DisposeAsync();
        _pw?.Dispose();
    }
}
