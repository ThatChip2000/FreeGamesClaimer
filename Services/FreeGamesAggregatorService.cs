using FreeGamesClaimer.Models;

namespace FreeGamesClaimer.Services;

public sealed record OffersRefreshResult(
    List<FreeGameOffer> Offers,
    DateTimeOffset FetchedAtUtc,
    bool FromCache,
    bool IsCacheFresh,
    List<string> Warnings);

public sealed class FreeGamesAggregatorService : IDisposable
{
    private readonly EpicFreeGamesService _epicService = new();
    private readonly SteamFreeGamesService _steamService = new();
    private readonly GogFreeGamesService _gogService = new();
    private readonly LocalStorageService _storage;

    public const int StoreCount = 3;

    public FreeGamesAggregatorService(LocalStorageService storage)
    {
        _storage = storage;
    }

    public OffersRefreshResult? TryLoadCachedOffers(StoreLocaleInfo locale, UserStateData state)
    {
        var cache = _storage.LoadCache();
        if (cache is null) return null;

        bool isFresh = _storage.IsCacheFresh(cache, locale);
        var validOffers = FilterNotExpired(cache.Offers);
        ApplyClaimedState(validOffers, state);

        return new OffersRefreshResult(
            Offers: validOffers,
            FetchedAtUtc: cache.FetchedAtUtc,
            FromCache: true,
            IsCacheFresh: isFresh,
            Warnings: new List<string>());
    }

    public async Task<OffersRefreshResult> FetchLiveOffersAsync(
        StoreLocaleInfo locale,
        UserStateData state,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var offers = new List<FreeGameOffer>();

        var epicTask = FetchSafeAsync(
            () => _epicService.GetCurrentFreeGamesAsync(locale, cancellationToken),
            "Epic Games",
            warnings);

        var steamTask = FetchSafeAsync(
            () => _steamService.GetCurrentFreeGamesAsync(locale, cancellationToken),
            "Steam",
            warnings);

        var gogTask = FetchSafeAsync(
            () => _gogService.GetCurrentFreeGamesAsync(locale, cancellationToken),
            "GOG",
            warnings);

        var results = await Task.WhenAll(epicTask, steamTask, gogTask).ConfigureAwait(false);
        offers.AddRange(results[0]);
        offers.AddRange(results[1]);
        offers.AddRange(results[2]);

        var nowUtc = DateTimeOffset.UtcNow;

        // Solo sobrescribe la caché si al menos una tienda respondió sin error
        if (warnings.Count < StoreCount)
        {
            _storage.SaveCache(new CachedOffersData
            {
                SchemaVersion = CachedOffersData.CurrentSchemaVersion,
                FetchedAtUtc = nowUtc,
                CountryCode = locale.CountryCode,
                LanguageCode = locale.EpicLocale,
                Offers = offers
            });
        }

        ApplyClaimedState(offers, state);

        return new OffersRefreshResult(
            Offers: offers,
            FetchedAtUtc: nowUtc,
            FromCache: false,
            IsCacheFresh: warnings.Count < StoreCount,
            Warnings: warnings);
    }

    private static async Task<List<FreeGameOffer>> FetchSafeAsync(
        Func<Task<List<FreeGameOffer>>> action,
        string storeName,
        List<string> warnings)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            lock (warnings)
            {
                warnings.Add($"{storeName}: no se pudo consultar ({ex.Message})");
            }
            return new List<FreeGameOffer>();
        }
    }

    private static List<FreeGameOffer> FilterNotExpired(IEnumerable<FreeGameOffer> offers)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        return offers
            .Where(o => o.EndsAtUtc is null || o.EndsAtUtc.Value > nowUtc)
            .ToList();
    }

    private static void ApplyClaimedState(IEnumerable<FreeGameOffer> offers, UserStateData state)
    {
        foreach (var offer in offers)
        {
            offer.IsClaimed = state.ClaimedOffers.ContainsKey(offer.Id);
        }
    }

    public void Dispose()
    {
        _epicService.Dispose();
        _steamService.Dispose();
        _gogService.Dispose();
    }
}
