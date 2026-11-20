using System.IO;
using System.Text.Json;
using FreeGamesClaimer.Models;

namespace FreeGamesClaimer.Services;

public sealed class LocalStorageService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    public LocalStorageService()
    {
        AppPaths.EnsureDirectories();
    }

    public CachedOffersData? LoadCache()
    {
        try
        {
            if (!File.Exists(AppPaths.CacheFilePath))
            {
                return null;
            }

            var json = File.ReadAllText(AppPaths.CacheFilePath);
            return JsonSerializer.Deserialize<CachedOffersData>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public bool IsCacheFresh(CachedOffersData? cache, StoreLocaleInfo locale)
    {
        if (cache is null) return false;
        if (cache.SchemaVersion != CachedOffersData.CurrentSchemaVersion) return false;
        if (!string.Equals(cache.CountryCode, locale.CountryCode, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(cache.LanguageCode, locale.EpicLocale, StringComparison.OrdinalIgnoreCase)) return false;

        var nowUtc = DateTimeOffset.UtcNow;
        var age = nowUtc - cache.FetchedAtUtc;
        if (age < TimeSpan.Zero || age >= CacheTtl)
        {
            return false;
        }

        // Si alguna oferta guardada en caché ya venció (ej. cambio semanal), forzar recarga
        if (cache.Offers.Any(o => o.EndsAtUtc is not null && o.EndsAtUtc.Value <= nowUtc))
        {
            return false;
        }

        return true;
    }

    public void SaveCache(CachedOffersData data)
    {
        SaveAtomically(AppPaths.CacheFilePath, data);
    }

    public UserStateData LoadState()
    {
        try
        {
            if (!File.Exists(AppPaths.StateFilePath))
            {
                return new UserStateData();
            }

            var json = File.ReadAllText(AppPaths.StateFilePath);
            return JsonSerializer.Deserialize<UserStateData>(json, JsonOptions) ?? new UserStateData();
        }
        catch
        {
            return new UserStateData();
        }
    }

    public void SaveState(UserStateData state)
    {
        // Limpieza suave de registros muy antiguos (> 180 días) para mantener el archivo pequeño
        var cutoff = DateTimeOffset.UtcNow.AddDays(-180);
        var expiredKeys = state.ClaimedOffers
            .Where(kv => kv.Value < cutoff)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in expiredKeys)
        {
            state.ClaimedOffers.Remove(key);
        }

        SaveAtomically(AppPaths.StateFilePath, state);
    }

    private static void SaveAtomically<T>(string targetPath, T value)
    {
        try
        {
            AppPaths.EnsureDirectories();
            var tempPath = targetPath + ".tmp";
            var json = JsonSerializer.Serialize(value, JsonOptions);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch
        {
            // Evita romper la UX por errores transitorios de disco
        }
    }
}
