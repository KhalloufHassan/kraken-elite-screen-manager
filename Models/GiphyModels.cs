using System.Text.Json.Serialization;

namespace KrakenEliteScreenManager.Models;

public record GiphyResponse(
    [property: JsonPropertyName("data")] GiphyGif[] Data
);

public record GiphyGif(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("images")] GiphyImages Images
);

public record GiphyImages(
    [property: JsonPropertyName("fixed_height_small")] GiphyImageVariant FixedHeightSmall,
    [property: JsonPropertyName("original")] GiphyImageVariant Original
);

public record GiphyImageVariant(
    [property: JsonPropertyName("url")] string Url
);
