using System.IO;

namespace FreeGamesClaimer.Services;

/// <summary>
/// Todas las rutas persistentes viven estrictamente en %LOCALAPPDATA%\FreeGamesClaimer
/// (nunca dentro de OneDrive ni en la carpeta del proyecto) para proteger cookies y caché.
/// </summary>
public static class AppPaths
{
    public static string BaseDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FreeGamesClaimer");

    public static string WebView2UserDataDir { get; } = Path.Combine(BaseDataDir, "WebView2Data");
    public static string CacheFilePath { get; } = Path.Combine(BaseDataDir, "cache.json");
    public static string StateFilePath { get; } = Path.Combine(BaseDataDir, "state.json");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(BaseDataDir);
        Directory.CreateDirectory(WebView2UserDataDir);
    }
}
