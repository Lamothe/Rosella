using System.Text.Json;

namespace Rosella.Compositor;

/// <summary>Bing's daily image for one day, before any pixels are involved.</summary>
internal sealed record BingImage(string UrlBase, string Title, string Copyright, string StartDate);

/// <summary>
/// The Bing daily-image archive — the "Bing Wallpaper" picture, downloaded
/// for Australia (mkt=en-AU). The parsing and URL building are pure; the
/// service around them handles the network, the cache and the pixels.
/// </summary>
internal static class BingArchive
{
    /// <summary>The market the desktop asks for.</summary>
    internal const string Market = "en-AU";

    /// <summary>The archive query for today's image.</summary>
    internal static string RequestUrl() =>
        $"https://www.bing.com/HPImageArchive.aspx?format=js&idx=0&n=1&mkt={Market}";

    /// <summary>
    /// Parses an HPImageArchive response. Null when it carries no image or
    /// no urlbase — never throws, whatever the JSON's shape.
    /// </summary>
    internal static BingImage? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("images", out var images)
                || images.ValueKind != JsonValueKind.Array
                || images.GetArrayLength() == 0)
            {
                return null;
            }

            var image = images[0];
            if (image.ValueKind != JsonValueKind.Object
                || !image.TryGetProperty("urlbase", out var urlBase)
                || urlBase.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return new BingImage(
                urlBase.GetString() ?? string.Empty,
                Text(image, "title"),
                Text(image, "copyright"),
                Text(image, "startdate"));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            // Wrong-shaped or malformed: no picture today, never a crash.
            return null;
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>
    /// The full-size picture for a urlbase. UHD first — the Retina panel is
    /// 2880 wide and a 1920 wallpaper would be stretched to reach it.
    /// </summary>
    internal static string ImageUrl(string urlBase, bool ultraHighDefinition = true) =>
        $"https://www.bing.com{urlBase}_{(ultraHighDefinition ? "UHD" : "FHD")}.jpg";
}
