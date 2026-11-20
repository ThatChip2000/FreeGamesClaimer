using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace FreeGamesClaimer.Models;

public sealed class FreeGameOffer : INotifyPropertyChanged
{
    private bool _isClaimed;

    public string Id { get; set; } = string.Empty;
    public StorePlatform Platform { get; set; }
    public string Title { get; set; } = string.Empty;
    public string StoreUrl { get; set; } = string.Empty;
    public List<string>? AllowedSlugs { get; set; }
    public string? ImageUrl { get; set; }
    public string? OriginalPriceText { get; set; }
    public DateTimeOffset? EndsAtUtc { get; set; }

    [JsonIgnore]
    public bool IsClaimed
    {
        get => _isClaimed;
        set
        {
            if (_isClaimed == value) return;
            _isClaimed = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ClaimButtonText));
        }
    }

    [JsonIgnore]
    public string PlatformName => Platform switch
    {
        StorePlatform.Epic => "Epic Games",
        StorePlatform.Steam => "Steam",
        StorePlatform.Gog => "GOG",
        _ => Platform.ToString()
    };

    [JsonIgnore]
    public string ClaimButtonText => IsClaimed ? "✓ Reclamado" : "Marcar reclamado";

    [JsonIgnore]
    public string EndsAtDisplay
    {
        get
        {
            if (EndsAtUtc is null)
            {
                return "Tiempo limitado (ver ficha)";
            }

            var local = EndsAtUtc.Value.ToLocalTime();
            var remaining = EndsAtUtc.Value - DateTimeOffset.UtcNow;
            var dateStr = local.ToString("dd MMM, HH:mm", CultureInfo.CurrentUICulture);

            if (remaining <= TimeSpan.Zero)
            {
                return $"Finalizó ({dateStr})";
            }

            if (remaining.TotalDays >= 1)
            {
                int days = (int)Math.Floor(remaining.TotalDays);
                return $"Hasta {dateStr} ({days}d {remaining.Hours}h)";
            }

            return $"Hasta {dateStr} ({remaining.Hours}h {remaining.Minutes}m)";
        }
    }

    [JsonIgnore]
    public string PriceBadgeText =>
        string.IsNullOrWhiteSpace(OriginalPriceText)
            ? "100% gratis"
            : $"Antes {OriginalPriceText} → Gratis";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
