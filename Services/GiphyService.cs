using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using KrakenEliteScreenManager.Models;

namespace KrakenEliteScreenManager.Services;

public class GiphyService(HttpClient http)
{
    // Read at runtime from the environment — never hard-code or commit a key.
    // Get a free one at https://developers.giphy.com, then: export GIPHY_API_KEY=your_key
    private static string? ApiKey => Environment.GetEnvironmentVariable("GIPHY_API_KEY");

    public async Task<GiphyGif[]> SearchAsync(string query, CancellationToken ct = default)
    {
        var key = ApiKey;
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException(
                "GIPHY search needs an API key. Get a free one at https://developers.giphy.com and set " +
                "GIPHY_API_KEY in your environment (or just use Local GIF… instead).");

        var url = $"https://api.giphy.com/v1/gifs/search?api_key={Uri.EscapeDataString(key)}&q={Uri.EscapeDataString(query)}&limit=24&rating=g";

        using var response = await http.GetAsync(url, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden ||
            response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException(
                $"GIPHY rejected the key ({(int)response.StatusCode}). Check GIPHY_API_KEY — " +
                "get a valid one at https://developers.giphy.com");

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<GiphyResponse>(ct);
        return result?.Data ?? [];
    }

    public async Task<byte[]> DownloadAsync(string url, CancellationToken ct = default) =>
        await http.GetByteArrayAsync(url, ct);
}
