using FreeGamesClaimer.Models;

namespace FreeGamesClaimer.Services;

public sealed record NavigationCheckResult(
    bool IsAllowed,
    string? BlockReason = null,
    bool IsLauncherScheme = false,
    bool IsStoreRestriction = false);

/// <summary>
/// Política estricta por LISTA BLANCA (allowlist) para WebView2:
/// 1. Bloquea cualquier esquema distinto de https:// (steam://, epicgames://, com.epicgames.launcher://, goggalaxy://, etc.).
/// 2. En el marco principal, permite ÚNICAMENTE:
///    - Las fichas de los juegos gratis activos ofertados por la app (incluyendo multi-slugs y control de edad).
///    - Flujos de inicio de sesión, 2FA, OAuth, CAPTCHA y checkout/reclamo (incluyendo /freelicense y /account/licenses en Steam).
///    Cualquier otra página de la plataforma o sitio externo queda bloqueada por defecto.
/// </summary>
public static class NavigationGuard
{
    private static readonly HashSet<string> LauncherSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam",
        "epicgames",
        "com.epicgames.launcher",
        "goggalaxy"
    };

    // Códigos ISO-639-1 de 2 letras usados por tiendas (excluyendo explícitamente "id" para no interferir con /id/login de Epic)
    private static readonly HashSet<string> KnownTwoLetterLocaleCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ar", "bg", "cs", "da", "de", "el", "en", "es", "fi", "fr",
        "he", "hr", "hu", "it", "ja", "ko", "nl", "no", "pl", "pt",
        "ro", "ru", "sk", "sl", "sv", "th", "tr", "uk", "vi", "zh"
    };

    private static readonly HashSet<string> EpicStoreAllowedSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "free-games", "purchase", "cart", "checkout", "login", "logout", "age-gate",
        "id", "auth", "oauth", "callback", "sso", "session", "authorize", "redirect",
        "store-login", "cdn-cgi", "api"
    };

    private static readonly HashSet<string> SteamStoreAllowedSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "login", "join", "logout", "checkout", "cart", "twofactor", "phone", "verify",
        "freelicense", "auth", "oauth", "openid", "cdn-cgi"
    };

    private static readonly HashSet<string> SteamAllowedAccountSubpaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "licenses", "ackgift"
    };

    private static readonly HashSet<string> SteamCommunityAllowedSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "login", "openid", "oauth", "mobilelogin", "cdn-cgi"
    };

    private static readonly HashSet<string> GogStoreAllowedSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "giveaway", "checkout", "order", "account", "login", "auth", "logout",
        "oauth", "callback", "sso", "cdn-cgi", "on_login_success"
    };

    private static readonly HashSet<string> AllowedExactAuthAndPaymentHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        // Epic auth / checkout
        "accounts.epicgames.com",
        "payment-website-pci.ol.epicgames.com",
        "launcher-public-service-prod06.ol.epicgames.com",
        // Steam auth / checkout
        "login.steampowered.com",
        "checkout.steampowered.com",
        // GOG auth
        "auth.gog.com",
        "login.gog.com",
        "embed.gog.com",
        // OAuth / Identity providers
        "accounts.google.com",
        "accounts.youtube.com",
        "login.live.com",
        "login.microsoftonline.com",
        "account.live.com",
        "appleid.apple.com",
        "idmsa.apple.com",
        "my.account.sony.com",
        "ca.account.sony.com",
        "auth.api.sonyentertainmentnetwork.com",
        "accounts.nintendo.com",
        "id.twitch.tv",
        // CAPTCHA / Anti-bot
        "hcaptcha.com",
        "challenges.cloudflare.com",
        "www.recaptcha.net"
    };

    private static readonly string[] AllowedAuthAndPaymentHostSuffixes =
    [
        ".hcaptcha.com",
        ".arkoselabs.com",
        ".funcaptcha.com",
        ".stripe.com",
        ".adyen.com",
        ".paypal.com",
        ".xsolla.com",
        ".cardinalcommerce.com"
    ];

    public static NavigationCheckResult Evaluate(
        string? rawUri,
        bool isSubFrame = false,
        IEnumerable<FreeGameOffer>? allowedOffers = null)
    {
        if (string.IsNullOrWhiteSpace(rawUri))
        {
            return new NavigationCheckResult(false, "URI vacía bloqueada.");
        }

        if (string.Equals(rawUri, "about:blank", StringComparison.OrdinalIgnoreCase))
        {
            return new NavigationCheckResult(true);
        }

        // Esquemas internos legítimos usados por sub-frames de tiendas y CAPTCHAs (hCaptcha / Cloudflare)
        if (isSubFrame)
        {
            if (string.Equals(rawUri, "about:srcdoc", StringComparison.OrdinalIgnoreCase) ||
                rawUri.StartsWith("blob:https://", StringComparison.OrdinalIgnoreCase))
            {
                return new NavigationCheckResult(true);
            }
        }

        if (!Uri.TryCreate(rawUri, UriKind.Absolute, out var uri))
        {
            return new NavigationCheckResult(false, "Dirección inválida bloqueada.");
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            if (LauncherSchemes.Contains(uri.Scheme))
            {
                return new NavigationCheckResult(
                    false,
                    $"Se bloqueó el intento de abrir el launcher externo ({uri.Scheme}://).",
                    IsLauncherScheme: true);
            }

            return new NavigationCheckResult(
                false,
                $"Protocolo no permitido bloqueado ({uri.Scheme}://). Solo se permite HTTPS.");
        }

        // Las restricciones de rutas solo aplican al marco principal, nunca a sub-frames
        // (para no romper CAPTCHAs, pasarelas PCI de checkout o widgets de autenticación).
        if (isSubFrame || allowedOffers is null)
        {
            return new NavigationCheckResult(true);
        }

        return EvaluateStrictAllowlist(uri, allowedOffers);
    }

    private static NavigationCheckResult EvaluateStrictAllowlist(Uri uri, IEnumerable<FreeGameOffer> allowedOffers)
    {
        var host = uri.Host.ToLowerInvariant();
        var segments = GetNormalizedPathSegments(uri);

        // 1. Epic Games Store (store.epicgames.com) — lista blanca estricta
        if (host == "store.epicgames.com")
        {
            var effective = StripLeadingLocale(segments);
            if (effective.Length > 0 && string.Equals(effective[0], "store", StringComparison.OrdinalIgnoreCase))
            {
                effective = StripLeadingLocale(effective[1..]);
            }

            if (effective.Length == 0)
            {
                return Restricted("Navegación bloqueada: solo puedes ver los juegos gratis ofertados y páginas de sesión/compra.");
            }

            var first = effective[0];
            if (EpicStoreAllowedSections.Contains(first))
            {
                return new NavigationCheckResult(true);
            }

            if ((string.Equals(first, "p", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(first, "bundles", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(first, "product", StringComparison.OrdinalIgnoreCase)) &&
                effective.Length >= 2)
            {
                var targetSlug = NormalizeSlugSegment(effective[1]);
                return IsEpicSlugAllowed(targetSlug, allowedOffers)
                    ? new NavigationCheckResult(true)
                    : Restricted($"Juego bloqueado ({uri.AbsolutePath}): solo puedes abrir los juegos gratis ofertados en la lista.");
            }

            return Restricted($"Sección bloqueada en Epic Games ({uri.AbsolutePath}): solo se permiten los juegos ofertados, sesión y compra.");
        }

        // 2. Epic Games ID / Auth (www.epicgames.com) — solo /id/..., /account/... (post-login) y /cdn-cgi/...
        if (host == "www.epicgames.com")
        {
            var effective = StripLeadingLocale(segments);
            if (effective.Length > 0 &&
                (string.Equals(effective[0], "id", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(effective[0], "account", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(effective[0], "cdn-cgi", StringComparison.OrdinalIgnoreCase)))
            {
                return new NavigationCheckResult(true);
            }

            return Restricted($"Sección bloqueada en Epic Games ({uri.AbsolutePath}): solo se permite inicio de sesión y cuenta (/id, /account).");
        }

        // 3. Steam Store (store.steampowered.com) — lista blanca estricta
        if (host == "store.steampowered.com")
        {
            if (segments.Length == 0)
            {
                return Restricted("Navegación bloqueada: solo puedes ver los juegos gratis ofertados y páginas de sesión/compra.");
            }

            var first = segments[0];
            if (SteamStoreAllowedSections.Contains(first))
            {
                return new NavigationCheckResult(true);
            }

            // Permitir /account (estado tras login), /account/licenses y /account/ackgift (flujo de licencias Free-to-Keep)
            if (string.Equals(first, "account", StringComparison.OrdinalIgnoreCase))
            {
                if (segments.Length == 1 ||
                    (segments.Length == 2 && SteamAllowedAccountSubpaths.Contains(segments[1])))
                {
                    return new NavigationCheckResult(true);
                }
            }

            var steamProduct = ExtractSteamProductKey(segments);
            if (steamProduct is not null)
            {
                return IsSteamProductAllowed(steamProduct, allowedOffers)
                    ? new NavigationCheckResult(true)
                    : Restricted("Juego bloqueado: solo puedes abrir los juegos gratis ofertados en la lista.");
            }

            return Restricted("Sección bloqueada en Steam: solo se permiten los juegos ofertados, sesión y compra.");
        }

        // 4. Steam Community (steamcommunity.com) — flujos de login/OpenID y aterrizaje post-login (/id/{alias}, /profiles/{id})
        if (host == "steamcommunity.com")
        {
            if (segments.Length > 0)
            {
                if (SteamCommunityAllowedSections.Contains(segments[0]))
                {
                    return new NavigationCheckResult(true);
                }

                if ((string.Equals(segments[0], "id", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(segments[0], "profiles", StringComparison.OrdinalIgnoreCase)) &&
                    segments.Length <= 2)
                {
                    return new NavigationCheckResult(true);
                }
            }

            return Restricted("Sección bloqueada en Steam Community: solo se permite inicio de sesión.");
        }

        // 5. GOG (www.gog.com / gog.com) — lista blanca estricta
        if (host is "www.gog.com" or "gog.com")
        {
            var effective = StripLeadingLocale(segments);

            // Permitir portada de idioma (/{lang}) porque GOG abre su modal de login (#openlogin)
            // y sus banners flash (#giveaway) en esa ruta.
            if (effective.Length == 0)
            {
                return new NavigationCheckResult(true);
            }

            var first = effective[0];
            if (GogStoreAllowedSections.Contains(first))
            {
                return new NavigationCheckResult(true);
            }

            if (string.Equals(first, "game", StringComparison.OrdinalIgnoreCase) && effective.Length >= 2)
            {
                var targetSlug = NormalizeGogSlug(effective[1]);
                return IsGogSlugAllowed(targetSlug, allowedOffers)
                    ? new NavigationCheckResult(true)
                    : Restricted("Juego bloqueado: solo puedes abrir los juegos gratis ofertados en la lista.");
            }

            return Restricted("Sección bloqueada en GOG: solo se permiten los juegos ofertados, sesión y compra.");
        }

        // 6. Proveedores de identidad (OAuth), CAPTCHAs y pasarelas de pago/verificación
        if (IsAllowedAuthOrPaymentHost(host, segments))
        {
            return new NavigationCheckResult(true);
        }

        return Restricted($"Sitio externo bloqueado ({host}): esta app solo abre los juegos ofertados y sus páginas de sesión.");
    }

    private static bool IsAllowedAuthOrPaymentHost(string host, string[] segments)
    {
        if (AllowedExactAuthAndPaymentHosts.Contains(host))
        {
            return true;
        }

        foreach (var suffix in AllowedAuthAndPaymentHostSuffixes)
        {
            if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if ((host is "www.facebook.com" or "m.facebook.com") &&
            segments.Length > 0 &&
            (string.Equals(segments[0], "login", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(segments[0], "login.php", StringComparison.OrdinalIgnoreCase) ||
             segments.Any(s => string.Equals(s, "oauth", StringComparison.OrdinalIgnoreCase))))
        {
            return true;
        }

        if (host == "discord.com" &&
            segments.Length > 0 &&
            (string.Equals(segments[0], "oauth2", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(segments[0], "login", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (host == "www.google.com" &&
            segments.Length > 0 &&
            string.Equals(segments[0], "recaptcha", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static NavigationCheckResult Restricted(string message) =>
        new(false, message, IsStoreRestriction: true);

    private static bool IsEpicSlugAllowed(string targetSlug, IEnumerable<FreeGameOffer> allowedOffers)
    {
        if (string.IsNullOrEmpty(targetSlug)) return false;

        bool hasUnresolvedEpicFallback = false;

        foreach (var offer in allowedOffers)
        {
            if (offer.Platform != StorePlatform.Epic) continue;

            // 1. Revisar todos los slugs conocidos de la oferta (offerMappings, catalogNs, productSlug, urlSlug)
            if (offer.AllowedSlugs is { Count: > 0 })
            {
                foreach (var slug in offer.AllowedSlugs)
                {
                    if (string.Equals(NormalizeSlugSegment(slug), targetSlug, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            // 2. Revisar también el slug presente en StoreUrl (con simetría de /store/ y /product/)
            if (Uri.TryCreate(offer.StoreUrl, UriKind.Absolute, out var offerUri))
            {
                var effective = StripLeadingLocale(GetNormalizedPathSegments(offerUri));
                if (effective.Length > 0 && string.Equals(effective[0], "store", StringComparison.OrdinalIgnoreCase))
                {
                    effective = StripLeadingLocale(effective[1..]);
                }

                if (effective.Length >= 2 &&
                    (string.Equals(effective[0], "p", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(effective[0], "bundles", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(effective[0], "product", StringComparison.OrdinalIgnoreCase)))
                {
                    var allowedSlug = NormalizeSlugSegment(effective[1]);
                    if (string.Equals(allowedSlug, targetSlug, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                else if (effective.Length == 1 &&
                         string.Equals(effective[0], "free-games", StringComparison.OrdinalIgnoreCase) &&
                         (offer.AllowedSlugs is null || offer.AllowedSlugs.Count == 0))
                {
                    hasUnresolvedEpicFallback = true;
                }
            }
        }

        // Si alguna oferta activa de Epic no tenía ningún slug válido en la API y cayó al fallback /free-games,
        // permitir abrir su ficha para que el usuario pueda reclamarla.
        return hasUnresolvedEpicFallback;
    }

    private static bool IsSteamProductAllowed(string targetProductKey, IEnumerable<FreeGameOffer> allowedOffers)
    {
        foreach (var offer in allowedOffers)
        {
            if (offer.Platform != StorePlatform.Steam) continue;
            if (!Uri.TryCreate(offer.StoreUrl, UriKind.Absolute, out var offerUri)) continue;

            var allowedKey = ExtractSteamProductKey(GetNormalizedPathSegments(offerUri));
            if (string.Equals(allowedKey, targetProductKey, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsGogSlugAllowed(string targetNormalizedSlug, IEnumerable<FreeGameOffer> allowedOffers)
    {
        if (string.IsNullOrEmpty(targetNormalizedSlug)) return false;

        bool hasUnresolvedGiveawayBanner = false;

        foreach (var offer in allowedOffers)
        {
            if (offer.Platform != StorePlatform.Gog) continue;

            if (offer.AllowedSlugs is { Count: > 0 })
            {
                foreach (var slug in offer.AllowedSlugs)
                {
                    if (string.Equals(NormalizeGogSlug(slug), targetNormalizedSlug, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            if (Uri.TryCreate(offer.StoreUrl, UriKind.Absolute, out var offerUri))
            {
                var effective = StripLeadingLocale(GetNormalizedPathSegments(offerUri));
                if (effective.Length >= 2 &&
                    string.Equals(effective[0], "game", StringComparison.OrdinalIgnoreCase))
                {
                    var allowedSlug = NormalizeGogSlug(effective[1]);
                    if (string.Equals(allowedSlug, targetNormalizedSlug, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                else if (string.Equals(offerUri.Fragment, "#giveaway", StringComparison.OrdinalIgnoreCase) &&
                         (offer.AllowedSlugs is null || offer.AllowedSlugs.Count == 0))
                {
                    hasUnresolvedGiveawayBanner = true;
                }
            }
        }

        return hasUnresolvedGiveawayBanner;
    }

    private static string? ExtractSteamProductKey(string[] segments)
    {
        int offset = 0;
        if (segments.Length >= 3 &&
            (string.Equals(segments[0], "agecheck", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(segments[0], "agecheckset", StringComparison.OrdinalIgnoreCase)))
        {
            offset = 1;
        }

        if (segments.Length >= offset + 2)
        {
            var kind = segments[offset].ToLowerInvariant();
            if (kind is "app" or "sub" or "bundle")
            {
                var id = segments[offset + 1];
                if (id.All(char.IsDigit) && id.Length > 0)
                {
                    return $"{kind}/{id}";
                }
            }
        }

        return null;
    }

    private static string[] GetNormalizedPathSegments(Uri uri) =>
        uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string[] StripLeadingLocale(string[] segments)
    {
        if (segments.Length > 0 && IsLocaleSegment(segments[0]))
        {
            return segments[1..];
        }
        return segments;
    }

    internal static bool IsLocaleSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment)) return false;

        // 1. Códigos ISO-2 conocidos (excluye "id" para no interferir con /id/login)
        if (segment.Length == 2)
        {
            return KnownTwoLetterLocaleCodes.Contains(segment);
        }

        // 2. Códigos de cultura estándar con guion: xx-YY (es-AR, en-GB, pt-PT), xx-419 (es-419), xx-YYYY (zh-Hans, zh-Hant)
        int dash = segment.IndexOf('-');
        if (dash == 2 && segment.LastIndexOf('-') == 2)
        {
            string langPart = segment[..2];
            string regionPart = segment[3..];

            if (!IsAsciiLetters(langPart)) return false;

            // xx-YY (2 letras) o xx-YYYY (4 letras como Hans/Hant)
            if ((regionPart.Length == 2 || regionPart.Length == 4) && IsAsciiLetters(regionPart))
            {
                return true;
            }

            // xx-NNN (3 dígitos como es-419)
            if (regionPart.Length == 3 && regionPart.All(char.IsAsciiDigit))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAsciiLetters(string value)
    {
        foreach (char c in value)
        {
            if (!char.IsAsciiLetter(c)) return false;
        }
        return value.Length > 0;
    }

    private static string NormalizeSlugSegment(string segment) =>
        Uri.UnescapeDataString(segment).Trim().ToLowerInvariant();

    private static string NormalizeGogSlug(string segment) =>
        NormalizeSlugSegment(segment).Replace('-', '_');
}
