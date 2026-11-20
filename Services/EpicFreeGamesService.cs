using System.Net;
using System.Net.Http;
using System.Text.Json;
using FreeGamesClaimer.Models;

namespace FreeGamesClaimer.Services;

/// <summary>
/// Consulta pública de solo lectura (sin cookies ni sesión) a la API de promociones
/// gratuitas de Epic Games Store. Muestra únicamente juegos reclamables en el momento actual.
/// </summary>
public sealed class EpicFreeGamesService : IDisposable
{
    private readonly HttpClient _http;

    public EpicFreeGamesService()
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
            $"https://store-site-backend-static.ak.epicgames.com/freeGamesPromotions" +
            $"?locale={Uri.EscapeDataString(locale.EpicLocale)}" +
            $"&country={Uri.EscapeDataString(locale.CountryCode)}" +
            $"&allowCountries={Uri.EscapeDataString(locale.CountryCode)}";

        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var results = new List<FreeGameOffer>();
        if (!TryGetElements(doc.RootElement, out var elements))
        {
            return results;
        }

        var nowUtc = DateTimeOffset.UtcNow;

        foreach (var element in elements.EnumerateArray())
        {
            if (!TryParseActiveFreeOffer(element, locale, nowUtc, out var offer) || offer is null)
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

    private static bool TryGetElements(JsonElement root, out JsonElement elements)
    {
        elements = default;
        return root.TryGetProperty("data", out var data)
            && data.TryGetProperty("Catalog", out var catalog)
            && catalog.TryGetProperty("searchStore", out var searchStore)
            && searchStore.TryGetProperty("elements", out elements)
            && elements.ValueKind == JsonValueKind.Array;
    }

    private static bool TryParseActiveFreeOffer(
        JsonElement element,
        StoreLocaleInfo locale,
        DateTimeOffset nowUtc,
        out FreeGameOffer? offer)
    {
        offer = null;

        // 1. Verificar que el precio actual con descuento sea 0
        if (!element.TryGetProperty("price", out var priceEl) ||
            !priceEl.TryGetProperty("totalPrice", out var totalPriceEl))
        {
            return false;
        }

        int originalPrice = GetInt32(totalPriceEl, "originalPrice");
        int discountPrice = GetInt32(totalPriceEl, "discountPrice");

        if (discountPrice != 0)
        {
            return false;
        }

        // 2. Verificar que exista una promoción activa al 100% (discountPercentage == 0 en la API de Epic).
        // Esto excluye los Free-to-Play permanentes (que no tienen ventana promocional activa) pero
        // permite regalos semanales oficiales de Epic cuyo precio base figure en 0 en el catálogo.
        if (!TryFindActiveFreePromoEnd(element, nowUtc, out var endDateUtc))
        {
            return false;
        }

        string title = GetString(element, "title") ?? "Juego gratis en Epic";
        string rawId = GetString(element, "id") ?? title;
        string? offerType = GetString(element, "offerType");
        var allSlugs = CollectAllValidSlugs(element);
        string? slug = allSlugs.FirstOrDefault();

        string storeUrl;
        if (!string.IsNullOrWhiteSpace(slug))
        {
            string segment = string.Equals(offerType, "BUNDLE", StringComparison.OrdinalIgnoreCase)
                ? "bundles"
                : "p";
            storeUrl = $"https://store.epicgames.com/{locale.EpicLocale}/{segment}/{slug}";
        }
        else
        {
            storeUrl = locale.EpicStoreHomeUrl;
        }

        string? originalPriceFmt = null;
        if (originalPrice > 0 && totalPriceEl.TryGetProperty("fmtPrice", out var fmtPriceEl))
        {
            originalPriceFmt = GetString(fmtPriceEl, "originalPrice");
        }

        offer = new FreeGameOffer
        {
            Id = $"epic:{rawId}:{endDateUtc:yyyyMMddHHmm}",
            Platform = StorePlatform.Epic,
            Title = title,
            StoreUrl = storeUrl,
            AllowedSlugs = allSlugs.Count > 0 ? allSlugs : null,
            ImageUrl = ResolveImageUrl(element),
            OriginalPriceText = originalPriceFmt,
            EndsAtUtc = endDateUtc
        };

        return true;
    }

    private static bool TryFindActiveFreePromoEnd(
        JsonElement element,
        DateTimeOffset nowUtc,
        out DateTimeOffset endDateUtc)
    {
        endDateUtc = default;

        if (!element.TryGetProperty("promotions", out var promotions) ||
            promotions.ValueKind != JsonValueKind.Object ||
            !promotions.TryGetProperty("promotionalOffers", out var groups) ||
            groups.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var group in groups.EnumerateArray())
        {
            if (!group.TryGetProperty("promotionalOffers", out var promos) ||
                promos.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var promo in promos.EnumerateArray())
            {
                if (!promo.TryGetProperty("discountSetting", out var ds) ||
                    GetInt32(ds, "discountPercentage", defaultValue: -1) != 0)
                {
                    continue;
                }

                var startStr = GetString(promo, "startDate");
                var endStr = GetString(promo, "endDate");

                if (!DateTimeOffset.TryParse(startStr, out var startUtc) ||
                    !DateTimeOffset.TryParse(endStr, out var endUtc))
                {
                    continue;
                }

                if (startUtc <= nowUtc && endUtc > nowUtc)
                {
                    endDateUtc = endUtc;
                    return true;
                }
            }
        }

        return false;
    }

    private static List<string> CollectAllValidSlugs(JsonElement element)
    {
        var slugs = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string? raw)
        {
            var normalized = NormalizeSlug(raw);
            if (!string.IsNullOrWhiteSpace(normalized) && seen.Add(normalized))
            {
                slugs.Add(normalized);
            }
        }

        // Prioridad 1: offerMappings (productHome primero, luego el resto)
        CollectSlugsFromMappings(element, "offerMappings", TryAdd);

        // Prioridad 2: catalogNs.mappings
        if (element.TryGetProperty("catalogNs", out var catalogNs) &&
            catalogNs.ValueKind == JsonValueKind.Object)
        {
            CollectSlugsFromMappings(catalogNs, "mappings", TryAdd);
        }

        // Prioridad 3 y 4: productSlug y urlSlug
        TryAdd(GetString(element, "productSlug"));
        TryAdd(GetString(element, "urlSlug"));

        return slugs;
    }

    private static void CollectSlugsFromMappings(JsonElement parent, string propertyName, Action<string?> addSlug)
    {
        if (!parent.TryGetProperty(propertyName, out var mappings) ||
            mappings.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var secondary = new List<string>();
        foreach (var mapping in mappings.EnumerateArray())
        {
            var pageSlug = GetString(mapping, "pageSlug");
            if (string.IsNullOrWhiteSpace(pageSlug)) continue;

            var pageType = GetString(mapping, "pageType");
            if (string.Equals(pageType, "productHome", StringComparison.OrdinalIgnoreCase))
            {
                addSlug(pageSlug);
            }
            else
            {
                secondary.Add(pageSlug);
            }
        }

        foreach (var item in secondary)
        {
            addSlug(item);
        }
    }

    internal static string? NormalizeSlug(string? rawSlug)
    {
        if (string.IsNullOrWhiteSpace(rawSlug))
        {
            return null;
        }

        var normalized = rawSlug.Trim().Trim('/');
        if (normalized.EndsWith("/home", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^5].TrimEnd('/');
        }

        if (string.IsNullOrWhiteSpace(normalized) ||
            string.Equals(normalized, "home", StringComparison.OrdinalIgnoreCase) ||
            IsHexHash32(normalized))
        {
            return null;
        }

        return normalized;
    }

    private static bool IsHexHash32(string value)
    {
        if (value.Length != 32) return false;
        foreach (char c in value)
        {
            bool isHex = (c >= '0' && c <= '9') ||
                         (c >= 'a' && c <= 'f') ||
                         (c >= 'A' && c <= 'F');
            if (!isHex) return false;
        }
        return true;
    }

    private static string? ResolveImageUrl(JsonElement element)
    {
        if (!element.TryGetProperty("keyImages", out var images) ||
            images.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string[] preferredTypes = ["OfferImageWide", "DieselStoreFrontWide", "Thumbnail", "OfferImageTall"];
        var byType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? firstValid = null;

        foreach (var img in images.EnumerateArray())
        {
            var url = GetString(img, "url");
            if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            firstValid ??= url;
            var type = GetString(img, "type");
            if (!string.IsNullOrWhiteSpace(type) && !byType.ContainsKey(type))
            {
                byType[type] = url;
            }
        }

        foreach (var preferred in preferredTypes)
        {
            if (byType.TryGetValue(preferred, out var match))
            {
                return match;
            }
        }

        return firstValid;
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

    private static int GetInt32(JsonElement el, string prop, int defaultValue = 0)
    {
        if (el.ValueKind == JsonValueKind.Object &&
            el.TryGetProperty(prop, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out var num))
        {
            return num;
        }
        return defaultValue;
    }

    public void Dispose() => _http.Dispose();
}
