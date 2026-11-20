using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using FreeGamesClaimer.Models;

namespace FreeGamesClaimer.Services;

/// <summary>
/// Consulta pública de solo lectura (sin cookies ni sesión) a la búsqueda de ofertas
/// 100% gratis por tiempo limitado ("Free to Keep") en Steam, excluyendo Free-to-Play permanentes.
/// </summary>
public sealed partial class SteamFreeGamesService : IDisposable
{
    private readonly HttpClient _http;

    public SteamFreeGamesService()
    {
        var handler = new HttpClientHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All
        };

        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("FreeGamesClaimer/1.0");
    }

    public async Task<List<FreeGameOffer>> GetCurrentFreeGamesAsync(
        StoreLocaleInfo locale,
        CancellationToken cancellationToken = default)
    {
        var url =
            $"https://store.steampowered.com/search/results/" +
            $"?query&maxprice=free&specials=1&infinite=1" +
            $"&cc={Uri.EscapeDataString(locale.CountryCode)}" +
            $"&l={Uri.EscapeDataString(locale.SteamLanguage)}";

        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var results = new List<FreeGameOffer>();
        if (!doc.RootElement.TryGetProperty("results_html", out var htmlProp) ||
            htmlProp.ValueKind != JsonValueKind.String)
        {
            return results;
        }

        var html = htmlProp.GetString();
        if (string.IsNullOrWhiteSpace(html))
        {
            return results;
        }

        foreach (Match rowMatch in SearchRowRegex().Matches(html))
        {
            var rowHtml = rowMatch.Value;

            // Verificar estrictamente 100% de descuento y precio final 0
            var discountMatch = DiscountAttrRegex().Match(rowHtml);
            var finalPriceMatch = FinalPriceAttrRegex().Match(rowHtml);
            var origPriceMatch = OriginalPriceDivRegex().Match(rowHtml);

            if (!discountMatch.Success || discountMatch.Groups[1].Value != "100")
            {
                continue;
            }

            if (!finalPriceMatch.Success || finalPriceMatch.Groups[1].Value != "0")
            {
                continue;
            }

            var originalPriceText = origPriceMatch.Success
                ? WebUtility.HtmlDecode(origPriceMatch.Groups[1].Value).Trim()
                : null;

            // Garantiza que tenía precio original > 0 (excluye Free-to-Play permanentes)
            if (string.IsNullOrWhiteSpace(originalPriceText))
            {
                continue;
            }

            var hrefMatch = HrefRegex().Match(rowHtml);
            var titleMatch = TitleSpanRegex().Match(rowHtml);
            if (!hrefMatch.Success || !titleMatch.Success)
            {
                continue;
            }

            var rawHref = WebUtility.HtmlDecode(hrefMatch.Groups[1].Value).Trim();
            var cleanUrl = StripTrackingQuery(rawHref);
            if (!cleanUrl.StartsWith("https://store.steampowered.com/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var title = WebUtility.HtmlDecode(titleMatch.Groups[1].Value).Trim();
            var appIdMatch = ItemKeyRegex().Match(rowHtml);
            var uniqueKey = appIdMatch.Success ? appIdMatch.Groups[1].Value : cleanUrl;

            var imgMatch = CapsuleImgRegex().Match(rowHtml);
            var imgUrl = imgMatch.Success
                ? WebUtility.HtmlDecode(imgMatch.Groups[1].Value).Trim()
                : null;

            var offer = new FreeGameOffer
            {
                Id = $"steam:{uniqueKey}",
                Platform = StorePlatform.Steam,
                Title = title,
                StoreUrl = cleanUrl,
                ImageUrl = imgUrl,
                OriginalPriceText = originalPriceText,
                EndsAtUtc = null
            };

            if (results.All(existing => !string.Equals(existing.Id, offer.Id, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(offer);
            }
        }

        return results;
    }

    private static string StripTrackingQuery(string rawUrl)
    {
        if (Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
        {
            return $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";
        }
        return rawUrl;
    }

    [GeneratedRegex(@"<a\b[^>]*class=""[^""]*search_result_row[^""]*""[\s\S]*?</a>", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex SearchRowRegex();

    [GeneratedRegex(@"href=""([^""]+)""", RegexOptions.IgnoreCase, 500)]
    private static partial Regex HrefRegex();

    [GeneratedRegex(@"data-ds-itemkey=""([^""]+)""", RegexOptions.IgnoreCase, 500)]
    private static partial Regex ItemKeyRegex();

    [GeneratedRegex(@"<span\s+class=""title"">([\s\S]*?)</span>", RegexOptions.IgnoreCase, 500)]
    private static partial Regex TitleSpanRegex();

    [GeneratedRegex(@"<div\s+class=""[^""]*search_capsule[^""]*"">[\s\S]*?<img\b[^>]*\bsrc=""([^""]+)""", RegexOptions.IgnoreCase, 500)]
    private static partial Regex CapsuleImgRegex();

    [GeneratedRegex(@"data-discount=""(\d+)""", RegexOptions.IgnoreCase, 500)]
    private static partial Regex DiscountAttrRegex();

    [GeneratedRegex(@"data-price-final=""(\d+)""", RegexOptions.IgnoreCase, 500)]
    private static partial Regex FinalPriceAttrRegex();

    [GeneratedRegex(@"<div\s+class=""[^""]*discount_original_price[^""]*"">([\s\S]*?)</div>", RegexOptions.IgnoreCase, 500)]
    private static partial Regex OriginalPriceDivRegex();

    public void Dispose() => _http.Dispose();
}
