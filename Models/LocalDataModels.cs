namespace FreeGamesClaimer.Models;

public sealed class CachedOffersData
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; }
    public DateTimeOffset FetchedAtUtc { get; set; }
    public string CountryCode { get; set; } = string.Empty;
    public string LanguageCode { get; set; } = string.Empty;
    public List<FreeGameOffer> Offers { get; set; } = new();
}

public sealed class UserStateData
{
    /// <summary>
    /// Mapa de OfferId -> fecha UTC en que el usuario lo marcó como reclamado.
    /// </summary>
    public Dictionary<string, DateTimeOffset> ClaimedOffers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
