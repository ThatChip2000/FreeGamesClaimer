using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using FreeGamesClaimer.Models;

namespace FreeGamesClaimer.Services;

/// <summary>
/// Consulta pública de solo lectura (sin cookies ni sesión) a GOG:
/// 1. Catálogo oficial (catalog.gog.com/v1/catalog) para juegos con -100% de descuento y precio base > 0.
/// 2. Comprobación ligera de banner de giveaway activo en la portada pública (www.gog.com).
/// </summary>
public sealed partial class GogFreeGamesService : IDisposable
{
    private readonly HttpClient _http;

    public GogFreeGamesService()
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
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/html");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("FreeGamesClaimer/1.0");
    }

    public async Task<List<FreeGameOffer>> GetCurrentFreeGamesAsync(
        StoreLocaleInfo locale,
        CancellationToken cancellationToken = default)
    {
        var results = await FetchCatalogFreebiesAsync(locale, cancellationToken).ConfigureAwait(false);

        // Comprobación complementaria para giveaways flash de portada (gog.com/#giveaway)
        var bannerGiveaway = await TryFetchHomepageGiveawayAsync(locale, cancellationToken).ConfigureAwait(false);
        if (bannerGiveaway is not null &&
            results.All(r => !string.Equals(r.Title, bannerGiveaway.Title, StringComparison.OrdinalIgnoreCase)))
        {
            results.Insert(0, bannerGiveaway);
        }

        return results;
    }

    private async Task<List<FreeGameOffer>> FetchCatalogFreebiesAsync(
        StoreLocaleInfo locale,
        CancellationToken cancellationToken)
    {
        var url =
            $"https://catalog.gog.com/v1/catalog" +
            $"?limit=20&order=desc:trending&discounted=eq:true&price=between:0,0" +
            $"&countryCode={Uri.EscapeDataString(locale.CountryCode)}" +
            $"&locale={Uri.EscapeDataString(locale.GogLocale)}" +
            $"&currencyCode=USD";

        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var results = new List<FreeGameOffer>();
        if (!doc.RootElement.TryGetProperty("products", out var products) ||
            products.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var product in products.EnumerateArray())
        {
            if (!TryParseCatalogOffer(product, locale, out var offer) || offer is null)
            {
                continue;
            }

            if (results.All(existing => !string.Equals(existing.Id, offer.Id, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(offer);
            }
        }

        return results;
    }

    private static bool TryParseCatalogOffer(
        JsonElement product,
        StoreLocaleInfo locale,
        out FreeGameOffer? offer)
    {
        offer = null;

        if (!product.TryGetProperty("price", out var priceEl) ||
            priceEl.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var discount = GetString(priceEl, "discount");
        if (!string.Equals(discount, "-100%", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!TryGetMoneyAmount(priceEl, "finalMoney", out var finalAmount) || finalAmount != 0m)
        {
            return false;
        }

        if (!TryGetMoneyAmount(priceEl, "baseMoney", out var baseAmount) || baseAmount <= 0m)
        {
            return false;
        }

        var title = GetString(product, "title");
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var id = GetString(product, "id") ?? title;
        var slug = GetString(product, "slug")?.Trim().Trim('/');
        var apiStoreLink = GetString(product, "storeLink");

        var allowedSlugs = new List<string>();
        if (!string.IsNullOrWhiteSpace(slug))
        {
            allowedSlugs.Add(slug);
        }

        var linkSlug = ExtractGogSlugFromUrl(apiStoreLink);
        if (!string.IsNullOrWhiteSpace(linkSlug) &&
            allowedSlugs.All(s => !string.Equals(s, linkSlug, StringComparison.OrdinalIgnoreCase)))
        {
            allowedSlugs.Add(linkSlug);
        }

        // Priorizar URL localizada según idioma de Windows (apiStoreLink suele venir fijo en /en/game/...)
        string storeUrl;
        if (!string.IsNullOrWhiteSpace(slug))
        {
            storeUrl = $"https://www.gog.com/{locale.GogLangSegment}/game/{slug}";
        }
        else if (!string.IsNullOrWhiteSpace(apiStoreLink) &&
                 apiStoreLink.StartsWith("https://www.gog.com/", StringComparison.OrdinalIgnoreCase))
        {
            storeUrl = apiStoreLink;
        }
        else
        {
            storeUrl = locale.GogStoreHomeUrl;
        }

        var imageUrl = GetString(product, "coverHorizontal") ?? GetString(product, "coverVertical");
        if (imageUrl is not null && !imageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            imageUrl = null;
        }

        offer = new FreeGameOffer
        {
            Id = $"gog:{id}",
            Platform = StorePlatform.Gog,
            Title = title,
            StoreUrl = storeUrl,
            AllowedSlugs = allowedSlugs.Count > 0 ? allowedSlugs : null,
            ImageUrl = imageUrl,
            OriginalPriceText = GetString(priceEl, "base"),
            EndsAtUtc = null
        };

        return true;
    }

    private async Task<FreeGameOffer?> TryFetchHomepageGiveawayAsync(
        StoreLocaleInfo locale,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(locale.GogStoreHomeUrl, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(html))
            {
                return null;
            }

            // Solo detectar banners reales activos en el DOM (evita falsos positivos con clases CSS del bundle)
            var bannerMatch = GiveawayBannerTagRegex().Match(html);
            var idMatch = !bannerMatch.Success ? GiveawayIdAttrRegex().Match(html) : Match.Empty;
            if (!bannerMatch.Success && !idMatch.Success)
            {
                return null;
            }

            // Ventana acotada alrededor del banner para extraer título y enlace /game/{slug} sin capturar otros juegos
            int anchorIndex = bannerMatch.Success ? bannerMatch.Index : idMatch.Index;
            int snippetStart = Math.Max(0, anchorIndex - 300);
            int snippetLen = Math.Min(html.Length - snippetStart, 4500);
            string snippet = html.Substring(snippetStart, snippetLen);

            var titleMatch = GiveawayTitleRegex().Match(snippet);
            var extractedTitle = titleMatch.Success
                ? StripHtmlTags(WebUtility.HtmlDecode(titleMatch.Groups[1].Value)).Trim()
                : null;

            string title = !string.IsNullOrWhiteSpace(extractedTitle)
                ? extractedTitle
                : "Giveaway activo en GOG (ver banner)";

            // Extraer slug de /game/{slug} si el banner enlaza a una ficha concreta
            var gameLinkMatch = GiveawayGameSlugRegex().Match(snippet);
            var bannerSlug = gameLinkMatch.Success ? gameLinkMatch.Groups[1].Value.Trim() : null;

            // Evitar colisión de Id entre giveaways futuros cuando no se logra extraer un título ni slug específico
            string offerId = !string.IsNullOrWhiteSpace(bannerSlug)
                ? $"gog:giveaway:{bannerSlug.ToLowerInvariant()}"
                : !string.IsNullOrWhiteSpace(extractedTitle)
                    ? $"gog:giveaway:{extractedTitle.ToLowerInvariant()}"
                    : $"gog:giveaway:banner:{DateTimeOffset.UtcNow:yyyyMMdd}";

            return new FreeGameOffer
            {
                Id = offerId,
                Platform = StorePlatform.Gog,
                Title = title,
                StoreUrl = locale.GogGiveawayUrl,
                AllowedSlugs = !string.IsNullOrWhiteSpace(bannerSlug) ? [bannerSlug] : null,
                ImageUrl = null,
                OriginalPriceText = null,
                EndsAtUtc = null
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractGogSlugFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (string.Equals(segments[i], "game", StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(segments[i + 1]);
            }
        }

        return null;
    }

    private static bool TryGetMoneyAmount(JsonElement priceEl, string propName, out decimal amount)
    {
        amount = 0m;
        if (!priceEl.TryGetProperty(propName, out var moneyEl) ||
            moneyEl.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var amountStr = GetString(moneyEl, "amount");
        return decimal.TryParse(amountStr, NumberStyles.Number, CultureInfo.InvariantCulture, out amount);
    }

    private static string? GetString(JsonElement el, string prop)
    {
        if (el.ValueKind == JsonValueKind.Object &&
            el.TryGetProperty(prop, out var value) &&
            value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }
        return null;
    }

    private static string StripHtmlTags(string text) => HtmlTagsRegex().Replace(text, " ");

    [GeneratedRegex(@"<giveaway-banner\b", RegexOptions.IgnoreCase, 500)]
    private static partial Regex GiveawayBannerTagRegex();

    [GeneratedRegex(@"\bid=""giveaway""", RegexOptions.IgnoreCase, 500)]
    private static partial Regex GiveawayIdAttrRegex();

    [GeneratedRegex(@"class=""[^""]*giveaway-banner__title[^""]*""[^>]*>([\s\S]*?)</", RegexOptions.IgnoreCase, 500)]
    private static partial Regex GiveawayTitleRegex();

    [GeneratedRegex(@"/game/([a-zA-Z0-9_\-]+)", RegexOptions.IgnoreCase, 500)]
    private static partial Regex GiveawayGameSlugRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.IgnoreCase, 500)]
    private static partial Regex HtmlTagsRegex();

    public void Dispose() => _http.Dispose();
}
