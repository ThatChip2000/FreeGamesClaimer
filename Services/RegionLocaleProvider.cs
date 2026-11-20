using System.Globalization;

namespace FreeGamesClaimer.Services;

public sealed record StoreLocaleInfo(
    string CountryCode,
    string EpicLocale,
    string SteamLanguage,
    string GogLocale,
    string GogLangSegment,
    string DisplaySummary)
{
    public string EpicStoreHomeUrl => $"https://store.epicgames.com/{EpicLocale}/free-games";
    public string EpicLoginUrl =>
        $"https://www.epicgames.com/id/login?redirectUrl={Uri.EscapeDataString(EpicStoreHomeUrl)}";
    public string SteamLoginUrl => $"https://store.steampowered.com/login/?redir=account%2F&l={SteamLanguage}";
    public string GogStoreHomeUrl => $"https://www.gog.com/{GogLangSegment}";
    public string GogLoginUrl =>
        $"https://login.gog.com/auth?client_id=46755278331571209&response_type=code&redirect_uri={Uri.EscapeDataString($"https://www.gog.com/on_login_success?returnTo=/{GogLangSegment}")}";
    public string GogGiveawayUrl => $"https://www.gog.com/{GogLangSegment}#giveaway";
}

public static class RegionLocaleProvider
{
    private static readonly HashSet<string> LatamCountries = new(StringComparer.OrdinalIgnoreCase)
    {
        "AR", "BO", "CL", "CO", "CR", "CU", "DO", "EC", "SV", "GT",
        "HN", "MX", "NI", "PA", "PY", "PE", "PR", "UY", "VE"
    };

    public static StoreLocaleInfo DetectFromWindows()
    {
        string country = "US";
        try
        {
            var region = RegionInfo.CurrentRegion;
            if (!string.IsNullOrWhiteSpace(region.TwoLetterISORegionName) &&
                region.TwoLetterISORegionName.Length == 2)
            {
                country = region.TwoLetterISORegionName.ToUpperInvariant();
            }
        }
        catch
        {
            country = "US";
        }

        var culture = CultureInfo.CurrentUICulture;
        string lang = culture.TwoLetterISOLanguageName.ToLowerInvariant();
        bool isLatam = LatamCountries.Contains(country);

        string epicLocale = lang switch
        {
            "es" => isLatam ? "es-MX" : "es-ES",
            "pt" => "pt-BR",
            "fr" => "fr",
            "de" => "de",
            "it" => "it",
            "ja" => "ja",
            "ko" => "ko",
            "pl" => "pl",
            "ru" => "ru",
            "tr" => "tr",
            "zh" => "zh-CN",
            _ => "en-US"
        };

        string steamLang = lang switch
        {
            "es" => isLatam ? "latam" : "spanish",
            "pt" => country == "BR" ? "brazilian" : "portuguese",
            "fr" => "french",
            "de" => "german",
            "it" => "italian",
            "ja" => "japanese",
            "ko" => "koreana",
            "pl" => "polish",
            "ru" => "russian",
            "tr" => "turkish",
            "zh" => "schinese",
            _ => "english"
        };

        string gogLocale = lang switch
        {
            "es" => "es-ES",
            "fr" => "fr-FR",
            "de" => "de-DE",
            "pl" => "pl-PL",
            "ru" => "ru-RU",
            "zh" => "zh-Hans",
            _ => "en-US"
        };

        string gogLangSegment = lang switch
        {
            "es" => "es",
            "fr" => "fr",
            "de" => "de",
            "pl" => "pl",
            "ru" => "ru",
            "zh" => "zh",
            _ => "en"
        };

        return new StoreLocaleInfo(
            CountryCode: country,
            EpicLocale: epicLocale,
            SteamLanguage: steamLang,
            GogLocale: gogLocale,
            GogLangSegment: gogLangSegment,
            DisplaySummary: $"{country} · {epicLocale}");
    }
}
