using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Labs.Gif;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using KrakenEliteScreenManager.Models;
using KrakenEliteScreenManager.Services;

namespace KrakenEliteScreenManager;

/// <summary>
/// Config editor: pick the mode (GIF loop / stock coolant) and a background GIF,
/// then write the config + GIF to ~/.config/kraken-elite-screen-manager and (re)start the
/// systemd user service that actually drives the screen.
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
        LoadConfigIntoUi();
        RefreshStatus();
    }

    private void LoadConfigIntoUi()
    {
        var cfg = DashboardConfig.Load();
        ModeGif.IsChecked = cfg.Mode == DashboardMode.GifLoop;
        ModeDashboard.IsChecked = cfg.Mode == DashboardMode.Dashboard;
        ModeCoolant.IsChecked = cfg.Mode == DashboardMode.Coolant;
        UpdatePickerEnabled();
    }

    private void Mode_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => UpdatePickerEnabled();

    private void UpdatePickerEnabled()
    {
        bool gif = ModeGif.IsChecked == true;
        if (GifPicker is not null) GifPicker.IsEnabled = gif;
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
        SetStatus($"Background: {Path.GetFileName(path)} — Apply & Start to use it.");
    }

    private async Task RunSearchAsync()
    {
        var query = SearchBox.Text?.Trim();
        if (string.IsNullOrEmpty(query)) return;

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        ClearThumbnails();
        SetStatus("Searching…");

        GiphyGif[] results;
        try { results = await _giphy.SearchAsync(query, ct); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { SetStatus($"Search failed: {ex.Message}"); return; }

        if (results.Length == 0) { SetStatus("No results."); return; }

        var tasks = results.Select(g => LoadThumbnailAsync(g, ct)).ToArray();
        foreach (var t in tasks)
        {
            if (ct.IsCancellationRequested) return;
            try { ThumbnailPanel.Children.Add((await t).border); }
            catch (OperationCanceledException) { return; }
            catch { /* skip */ }
        }
        SetStatus("Click a GIF to pick it, then Apply & Start.");
    }

    private async Task<(Border border, string originalUrl)> LoadThumbnailAsync(GiphyGif gif, CancellationToken ct)
    {
        var bytes = await _giphy.DownloadAsync(gif.Images.FixedHeightSmall.Url, ct);
        var stream = new MemoryStream(bytes);
        var gifImage = new GifImage
        {
            Source = stream, Stretch = Stretch.UniformToFill, Width = 116, Height = 96,
            IterationCount = Avalonia.Animation.IterationCount.Infinite,
        };
        var border = new Border
        {
            Width = 120, Height = 100, Margin = new Avalonia.Thickness(2),
            BorderThickness = new Avalonia.Thickness(2), BorderBrush = Brushes.Transparent,
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
        clicked.BorderBrush = Brushes.Cyan;
        _selectedGifUrl = originalUrl;
        _selectedLocalPath = null;
        SetStatus("GIF picked — Apply & Start to use it.");
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
                     : DashboardMode.Coolant;

            if (cfg.Mode == DashboardMode.GifLoop)
            {
                if (!await SaveSelectedGifAsync() && !File.Exists(DashboardConfig.GifFile))
                {
                    SetStatus("Pick a GIF first (GIPHY or Local), or switch to Stock Coolant.");
                    return;
                }
            }

            cfg.Save();
            SetStatus("Saving config and (re)starting service…");
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
            await Task.Run(() => File.Copy(_selectedLocalPath, DashboardConfig.GifFile, overwrite: true));
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
        try { await Task.Run(SystemdManager.Stop); SetStatus("Service stopped."); }
        catch (Exception ex) { SetStatus($"Stop failed: {ex.Message}"); }
        finally { StopBtn.IsEnabled = true; RefreshStatus(); }
    }

    private void RefreshStatus()
    {
        _ = Task.Run(() =>
        {
            bool active = SystemdManager.IsActive();
            Dispatcher.UIThread.Post(() =>
                SetStatus(active ? "Service running — your screen reflects the saved config."
                                 : "Service not running. Apply & Start to launch it."));
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
