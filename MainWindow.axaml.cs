using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Labs.Gif;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using KrakenEliteScreenManager.Models;
using KrakenEliteScreenManager.Services;

namespace KrakenEliteScreenManager;

/// <summary>
/// Config editor: pick the mode + a background GIF + display settings, then write the
/// config (and GIF) to ~/.config/kraken-elite-screen-manager and (re)start the systemd
/// user service that actually drives the screen.
/// </summary>
public partial class MainWindow : Window
{
    private readonly HttpClient _http = new();
    private readonly GiphyService _giphy;

    private CancellationTokenSource? _searchCts;
    private Border? _selectedBorder;
    private string? _selectedGifUrl;    // GIPHY original URL
    private string? _selectedLocalPath; // local file

    public MainWindow()
    {
        InitializeComponent();
        _giphy = new GiphyService(_http);

        BrightnessSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
                BrightnessValue.Text = $"{(int)BrightnessSlider.Value}%";
        };
        DimSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
                DimValue.Text = $"{(int)DimSlider.Value}%";
        };

        LoadConfigIntoUi();
        RefreshStatus();

        // Field-rooted so it isn't garbage-collected (a local one stops ticking).
        // Renders the CURRENT (unsaved) settings every tick, so tweaks show live.
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _previewTimer.Tick += (_, _) => _ = RenderPreviewAsync();
        _previewTimer.Start();
    }

    private readonly DispatcherTimer _previewTimer;
    private readonly PreviewRenderer _preview = new();
    private Bitmap? _previewBmp;
    private bool _rendering;

    private DashboardMode CurrentMode() =>
        ModeGif.IsChecked == true ? DashboardMode.GifLoop
      : ModeDashboard.IsChecked == true ? DashboardMode.Dashboard
      : ModeGifDash.IsChecked == true ? DashboardMode.GifDashboard
      : ModeWeb.IsChecked == true ? DashboardMode.WebPage
      : ModeVideo.IsChecked == true ? DashboardMode.Video
      : DashboardMode.Coolant;

    private void PreviewToggle_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        bool show = PreviewPanel.IsVisible != true;
        PreviewPanel.IsVisible = show;
        PreviewToggle.Content = show ? "🙈 Hide preview" : "👁 Show preview";
    }

    private async Task RenderPreviewAsync()
    {
        if (_rendering || PreviewPanel.IsVisible != true) return; // skip work while hidden
        _rendering = true;
        try
        {
            var mode = CurrentMode();
            var style = (OverlayStyle)Math.Max(0, OverlayStyleCombo.SelectedIndex);
            int dim = (int)DimSlider.Value;
            string gifPath = _selectedLocalPath ?? (File.Exists(DashboardConfig.GifFile) ? DashboardConfig.GifFile : "");

            var png = await _preview.RenderAsync(mode, style, dim, gifPath,
                                                 WebUrlBox.Text?.Trim() ?? "", VideoPathBox.Text?.Trim() ?? "");
            if (png is null)
            {
                PreviewImage.Source = null;
                PreviewHint.Text = mode == DashboardMode.Coolant
                    ? "Stock coolant screen — no preview"
                    : "Pick a source to preview";
                return;
            }
            var bmp = new Bitmap(new MemoryStream(png));
            PreviewImage.Source = bmp;
            _previewBmp?.Dispose();
            _previewBmp = bmp;
            PreviewHint.Text = "live preview of current settings";
        }
        catch (Exception ex) { PreviewHint.Text = "preview: " + ex.Message; }
        finally { _rendering = false; }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _ = _preview.DisposeAsync();
    }

    private void LoadConfigIntoUi()
    {
        var cfg = DashboardConfig.Load();
        ModeGif.IsChecked       = cfg.Mode == DashboardMode.GifLoop;
        ModeDashboard.IsChecked = cfg.Mode == DashboardMode.Dashboard;
        ModeGifDash.IsChecked   = cfg.Mode == DashboardMode.GifDashboard;
        ModeWeb.IsChecked       = cfg.Mode == DashboardMode.WebPage;
        ModeVideo.IsChecked     = cfg.Mode == DashboardMode.Video;
        ModeCoolant.IsChecked   = cfg.Mode == DashboardMode.Coolant;
        if (ModeGif.IsChecked != true && ModeDashboard.IsChecked != true && ModeGifDash.IsChecked != true
            && ModeWeb.IsChecked != true && ModeVideo.IsChecked != true && ModeCoolant.IsChecked != true)
            ModeCoolant.IsChecked = true;

        BrightnessSlider.Value = Math.Clamp(cfg.Brightness, 10, 100);
        BrightnessValue.Text = $"{(int)BrightnessSlider.Value}%";
        RotationCombo.SelectedIndex = ((cfg.Rotation / 90) % 4 + 4) % 4;

        OverlayStyleCombo.SelectedIndex = (int)cfg.OverlayStyle;
        DimSlider.Value = Math.Clamp(cfg.OverlayDim, 0, 100);
        DimValue.Text = $"{(int)DimSlider.Value}%";

        WebUrlBox.Text = cfg.WebUrl;
        VideoPathBox.Text = cfg.VideoFile;
        int fi = Array.IndexOf(FpsValues, cfg.MaxFps);
        FpsCombo.SelectedIndex = fi >= 0 ? fi : 0;

        if (File.Exists(DashboardConfig.GifFile))
            SelectedLabel.Text = "Using your saved GIF (bg.gif). Pick a new one to replace it.";

        UpdatePickerVisible();
    }

    private void Mode_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => UpdatePickerVisible();

    private static readonly int[] FpsValues = { 0, 60, 30, 24, 15 }; // matches FpsCombo order (0 = max)

    private bool IsGifMode => ModeGif.IsChecked == true || ModeGifDash.IsChecked == true;

    private void UpdatePickerVisible()
    {
        if (GifPicker is not null) GifPicker.IsVisible = IsGifMode;
        if (LegibilityPanel is not null) LegibilityPanel.IsVisible = ModeGifDash.IsChecked == true;
        if (WebPanel is not null) WebPanel.IsVisible = ModeWeb.IsChecked == true;
        if (VideoPanel is not null) VideoPanel.IsVisible = ModeVideo.IsChecked == true;
    }

    private async void WebFileBtn_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose an HTML page",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Web page") { Patterns = new[] { "*.html", "*.htm" } } },
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path)) WebUrlBox.Text = path;
    }

    private async void VideoFileBtn_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a video file",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Video") { Patterns = new[] { "*.mp4", "*.mkv", "*.webm", "*.mov", "*.avi", "*.gif" } },
            },
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path)) VideoPathBox.Text = path;
    }

    // --- GIF selection --------------------------------------------------------

    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = RunSearchAsync();
    }

    private void SearchBtn_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _ = RunSearchAsync();

    private async void LocalBtn_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a background GIF/image",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Images") { Patterns = new[] { "*.gif", "*.png", "*.jpg", "*.jpeg", "*.webp" } },
            },
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        _selectedLocalPath = path;
        _selectedGifUrl = null;
        if (_selectedBorder is not null) _selectedBorder.BorderBrush = Brushes.Transparent;
        _selectedBorder = null;
        SelectedLabel.Text = $"📁 {Path.GetFileName(path)} — Apply & Start to use it.";
    }

    private async Task RunSearchAsync()
    {
        var query = SearchBox.Text?.Trim();
        if (string.IsNullOrEmpty(query)) return;

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        ClearThumbnails();
        SelectedLabel.Text = "Searching GIPHY…";

        GiphyGif[] results;
        try { results = await _giphy.SearchAsync(query, ct); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { SelectedLabel.Text = $"Search failed: {ex.Message}"; return; }

        if (results.Length == 0) { SelectedLabel.Text = "No results."; return; }

        var tasks = results.Select(g => LoadThumbnailAsync(g, ct)).ToArray();
        foreach (var t in tasks)
        {
            if (ct.IsCancellationRequested) return;
            try { ThumbnailPanel.Children.Add((await t).border); }
            catch (OperationCanceledException) { return; }
            catch { /* skip */ }
        }
        SelectedLabel.Text = "Click a GIF to pick it.";
    }

    private async Task<(Border border, string originalUrl)> LoadThumbnailAsync(GiphyGif gif, CancellationToken ct)
    {
        var bytes = await _giphy.DownloadAsync(gif.Images.FixedHeightSmall.Url, ct);
        var stream = new MemoryStream(bytes);
        var gifImage = new GifImage
        {
            Source = stream, Stretch = Stretch.UniformToFill, Width = 112, Height = 92,
            IterationCount = Avalonia.Animation.IterationCount.Infinite,
        };
        var border = new Border
        {
            Width = 120, Height = 100, Margin = new Avalonia.Thickness(4),
            BorderThickness = new Avalonia.Thickness(2), BorderBrush = Brushes.Transparent,
            CornerRadius = new Avalonia.CornerRadius(8),
            Background = Brushes.Black, Child = gifImage, Cursor = new Cursor(StandardCursorType.Hand),
            ClipToBounds = true, Tag = stream,
        };
        var url = gif.Images.Original.Url;
        border.PointerPressed += (_, _) => ThumbnailClicked(border, url);
        return (border, url);
    }

    private void ThumbnailClicked(Border clicked, string originalUrl)
    {
        if (_selectedBorder is not null) _selectedBorder.BorderBrush = Brushes.Transparent;
        _selectedBorder = clicked;
        clicked.BorderBrush = new SolidColorBrush(Color.Parse("#22D3EE"));
        _selectedGifUrl = originalUrl;
        _selectedLocalPath = null;
        SelectedLabel.Text = "GIPHY GIF picked — Apply & Start to use it.";
    }

    // --- apply / stop ---------------------------------------------------------

    private async void ApplyBtn_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ApplyBtn.IsEnabled = false;
        try
        {
            var cfg = DashboardConfig.Load();
            cfg.Mode = ModeGif.IsChecked == true ? DashboardMode.GifLoop
                     : ModeDashboard.IsChecked == true ? DashboardMode.Dashboard
                     : ModeGifDash.IsChecked == true ? DashboardMode.GifDashboard
                     : ModeWeb.IsChecked == true ? DashboardMode.WebPage
                     : ModeVideo.IsChecked == true ? DashboardMode.Video
                     : DashboardMode.Coolant;
            cfg.Brightness = (int)BrightnessSlider.Value;
            cfg.Rotation = Math.Max(0, RotationCombo.SelectedIndex) * 90;
            cfg.OverlayStyle = (OverlayStyle)Math.Max(0, OverlayStyleCombo.SelectedIndex);
            cfg.OverlayDim = (int)DimSlider.Value;
            cfg.MaxFps = FpsValues[Math.Clamp(FpsCombo.SelectedIndex, 0, FpsValues.Length - 1)];
            cfg.WebUrl = WebUrlBox.Text?.Trim() ?? "";
            cfg.VideoFile = VideoPathBox.Text?.Trim() ?? "";

            if (cfg.Mode is DashboardMode.GifLoop or DashboardMode.GifDashboard)
            {
                if (!await SaveSelectedGifAsync() && !File.Exists(DashboardConfig.GifFile))
                {
                    SetStatus("Pick a GIF first (file or GIPHY), or choose another mode.");
                    return;
                }
            }
            if (cfg.Mode == DashboardMode.WebPage && string.IsNullOrWhiteSpace(cfg.WebUrl))
            {
                SetStatus("Enter a URL (or pick an HTML file) for Web Page mode.");
                return;
            }
            if (cfg.Mode == DashboardMode.Video && !File.Exists(cfg.VideoFile))
            {
                SetStatus("Choose a valid video file for Video mode.");
                return;
            }

            cfg.Save();
            SetStatus("Saving config and (re)starting the service…");
            await Task.Run(SystemdManager.ApplyAndRestart);
            RefreshStatus();
        }
        catch (Exception ex)
        {
            SetStatus($"Apply failed: {ex.Message}");
        }
        finally { ApplyBtn.IsEnabled = true; }
    }

    /// <summary>Write the chosen GIF (GIPHY download or local copy) to the config dir. Returns false if none chosen.</summary>
    private async Task<bool> SaveSelectedGifAsync()
    {
        Directory.CreateDirectory(DashboardConfig.Dir);
        if (_selectedLocalPath is not null)
        {
            var src = _selectedLocalPath;
            await Task.Run(() => File.Copy(src, DashboardConfig.GifFile, overwrite: true));
            return true;
        }
        if (_selectedGifUrl is not null)
        {
            SetStatus("Downloading GIF…");
            var bytes = await _giphy.DownloadAsync(_selectedGifUrl);
            await File.WriteAllBytesAsync(DashboardConfig.GifFile, bytes);
            return true;
        }
        return false;
    }

    private async void StopBtn_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        StopBtn.IsEnabled = false;
        try { await Task.Run(SystemdManager.Stop); SetStatus("Service stopped — screen reverted to coolant."); }
        catch (Exception ex) { SetStatus($"Stop failed: {ex.Message}"); }
        finally { StopBtn.IsEnabled = true; RefreshStatus(); }
    }

    private void RefreshStatus()
    {
        _ = Task.Run(() =>
        {
            bool active = SystemdManager.IsActive();
            Dispatcher.UIThread.Post(() =>
            {
                StatusDot.Fill = new SolidColorBrush(Color.Parse(active ? "#34D399" : "#6E7889"));
                StatusPillText.Text = active ? "Running" : "Stopped";
            });
        });
    }

    private void ClearThumbnails()
    {
        foreach (var child in ThumbnailPanel.Children)
            if (child is Border b && b.Tag is MemoryStream ms) ms.Dispose();
        ThumbnailPanel.Children.Clear();
        _selectedBorder = null;
    }

    private void SetStatus(string message) => Dispatcher.UIThread.Post(() => StatusText.Text = message);
}
