using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FreeGamesClaimer.Models;
using FreeGamesClaimer.Services;
using FreeGamesClaimer.Windows;
using Microsoft.Web.WebView2.Core;

namespace FreeGamesClaimer;

public partial class MainWindow : Window
{
    private readonly StoreLocaleInfo _locale;
    private readonly LocalStorageService _storage;
    private readonly FreeGamesAggregatorService _aggregator;
    private readonly UserStateData _userState;

    private readonly ObservableCollection<FreeGameOffer> _epicOffers = new();
    private readonly ObservableCollection<FreeGameOffer> _steamOffers = new();
    private readonly ObservableCollection<FreeGameOffer> _gogOffers = new();

    private readonly DispatcherTimer _bannerHideTimer;
    private CancellationTokenSource? _refreshCts;
    private CoreWebView2Environment? _webViewEnvironment;
    private Task? _webViewInitTask;
    private DateTimeOffset _lastManualRefreshUtc = DateTimeOffset.MinValue;
    private string? _lastAllowedUrl;
    private bool _isRevertingBlockedNavigation;

    public MainWindow()
    {
        InitializeComponent();

        _locale = RegionLocaleProvider.DetectFromWindows();
        _storage = new LocalStorageService();
        _aggregator = new FreeGamesAggregatorService(_storage);
        _userState = _storage.LoadState();

        EpicGamesList.ItemsSource = _epicOffers;
        SteamGamesList.ItemsSource = _steamOffers;
        GogGamesList.ItemsSource = _gogOffers;
        LocaleText.Text = $"Región Windows: {_locale.DisplaySummary}";

        _bannerHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _bannerHideTimer.Tick += (_, _) =>
        {
            _bannerHideTimer.Stop();
            BlockedBanner.Visibility = Visibility.Collapsed;
        };

        PositionSafelyOnPrimaryMonitor();
        SourceInitialized += (_, _) => PositionSafelyOnPrimaryMonitor();
        Loaded += MainWindow_Loaded;
    }

    /// <summary>
    /// Coloca la ventana centrada dentro del área de trabajo visible del monitor principal,
    /// ajustando su tamaño a la resolución/DPI actuales para que la barra de título y los bordes
    /// de redimensionamiento nunca queden fuera de pantalla.
    /// </summary>
    private void PositionSafelyOnPrimaryMonitor()
    {
        var workArea = SystemParameters.WorkArea;
        if (workArea.Width <= 0 || workArea.Height <= 0)
        {
            return;
        }

        double maxSafeWidth = Math.Max(480, workArea.Width - 24);
        double maxSafeHeight = Math.Max(360, workArea.Height - 24);

        MinWidth = Math.Min(680, maxSafeWidth);
        MinHeight = Math.Min(460, maxSafeHeight);

        double targetWidth = Math.Min(1180, workArea.Width * 0.88);
        double targetHeight = Math.Min(740, workArea.Height * 0.88);

        Width = Math.Clamp(targetWidth, MinWidth, maxSafeWidth);
        Height = Math.Clamp(targetHeight, MinHeight, maxSafeHeight);

        Left = workArea.Left + Math.Max(0, (workArea.Width - Width) / 2);
        Top = workArea.Top + Math.Max(0, (workArea.Height - Height) / 2);
    }

    private IEnumerable<FreeGameOffer> GetAllCurrentOffers() =>
        _epicOffers.Concat(_steamOffers).Concat(_gogOffers);

    private bool HasDisplayedOffers() =>
        _epicOffers.Count > 0 || _steamOffers.Count > 0 || _gogOffers.Count > 0;

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // 1. Pintar caché local inmediatamente (una única lectura de disco) para arranque < 1 segundo
        var cached = _aggregator.TryLoadCachedOffers(_locale, _userState);
        if (cached is not null)
        {
            ApplyOffersToUi(cached);
        }

        // 2. Inicializar WebView2 en paralelo usando %LOCALAPPDATA%\FreeGamesClaimer\WebView2Data
        _webViewInitTask = InitializeWebViewAsync();

        // 3. Consultar ofertas públicas solo si no hay caché o si venció el TTL / expiró una oferta
        if (cached?.IsCacheFresh != true)
        {
            await RefreshOffersAsync(isManual: false);
        }
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            AppPaths.EnsureDirectories();
            _webViewEnvironment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: AppPaths.WebView2UserDataDir);

            await Browser.EnsureCoreWebView2Async(_webViewEnvironment);
            var core = Browser.CoreWebView2;

            // Política estricta: sin guardar contraseñas, sin autofill, sin tocar User-Agent ni inyectar scripts
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsStatusBarEnabled = false;

            core.DownloadStarting += Core_DownloadStarting;
            core.NavigationStarting += Core_NavigationStarting;
            core.FrameNavigationStarting += Core_FrameNavigationStarting;
            core.NavigationCompleted += Core_NavigationCompleted;
            core.SourceChanged += Core_SourceChanged;
            core.HistoryChanged += Core_HistoryChanged;
            core.NewWindowRequested += Core_NewWindowRequested;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowBlockedBanner(
                "No se encontró Microsoft Edge WebView2 Runtime en este equipo. " +
                "Instálalo desde developer.microsoft.com/microsoft-edge/webview2 para cargar las tiendas.");
        }
        catch (Exception ex)
        {
            ShowBlockedBanner($"No se pudo inicializar WebView2: {ex.Message}");
        }
    }

    private void Core_DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Cancel = true;
        e.Handled = true;
        ShowBlockedBanner("Descarga bloqueada: esta app solo reclama juegos en tu biblioteca, no descarga instaladores ni archivos.");
    }

    private void Core_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        var check = NavigationGuard.Evaluate(e.Uri, isSubFrame: false, GetAllCurrentOffers());
        if (!check.IsAllowed)
        {
            e.Cancel = true;
            if (!string.IsNullOrWhiteSpace(check.BlockReason))
            {
                ShowBlockedBanner(check.BlockReason);
            }
            return;
        }

        NavProgressBar.Visibility = Visibility.Visible;
    }

    private void Core_FrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        var check = NavigationGuard.Evaluate(e.Uri, isSubFrame: true);
        if (!check.IsAllowed)
        {
            e.Cancel = true;
            if (check.IsLauncherScheme && !string.IsNullOrWhiteSpace(check.BlockReason))
            {
                ShowBlockedBanner(check.BlockReason);
            }
        }
    }

    private void Core_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        NavProgressBar.Visibility = Visibility.Collapsed;
        UpdateNavButtons();
    }

    private void Core_SourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        var core = Browser.CoreWebView2;
        var source = core?.Source;
        if (string.IsNullOrWhiteSpace(source))
        {
            UpdateNavButtons();
            return;
        }

        // Protege también contra navegación SPA del lado cliente (history.pushState en Epic/GOG)
        // que no dispara NavigationStarting al hacer clic en otro juego o sección restringida.
        var currentOffers = GetAllCurrentOffers().ToList();
        var check = NavigationGuard.Evaluate(source, isSubFrame: false, currentOffers);
        if (!check.IsAllowed)
        {
            if (!string.IsNullOrWhiteSpace(check.BlockReason))
            {
                ShowBlockedBanner(check.BlockReason);
            }

            if (!_isRevertingBlockedNavigation && core is not null)
            {
                _isRevertingBlockedNavigation = true;
                try
                {
                    // Consumir _lastAllowedUrl antes de navegar: si la carga de retorno es válida se restaurará sola,
                    // y si redirige inmediatamente de vuelta a una URL bloqueada no entrará en bucle infinito.
                    var revertTarget = _lastAllowedUrl;
                    _lastAllowedUrl = null;

                    bool canRevertToLast =
                        !string.IsNullOrWhiteSpace(revertTarget) &&
                        !string.Equals(revertTarget, source, StringComparison.OrdinalIgnoreCase) &&
                        NavigationGuard.Evaluate(revertTarget, isSubFrame: false, currentOffers).IsAllowed;

                    if (canRevertToLast)
                    {
                        core.Navigate(revertTarget!);
                    }
                    else
                    {
                        // Si la última URL permitida ya expiró o causó redirección en bucle, volver limpio a la pantalla inicial
                        Browser.Visibility = Visibility.Collapsed;
                        WelcomeOverlay.Visibility = Visibility.Visible;
                        AddressBarText.Text = "Selecciona un juego o una tienda en el panel izquierdo";
                        core.Navigate("about:blank");
                    }
                }
                finally
                {
                    Dispatcher.BeginInvoke(() => _isRevertingBlockedNavigation = false, DispatcherPriority.Background);
                }
            }
            return;
        }

        if (source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            _lastAllowedUrl = source;
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            bool isHttps = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
            SecurityBadge.Text = isHttps ? "🔒 HTTPS" : uri.Scheme.ToUpperInvariant();
            AddressBarText.Text = uri.Host + uri.PathAndQuery;
        }
        else
        {
            AddressBarText.Text = source;
        }

        UpdateNavButtons();
    }

    private void Core_HistoryChanged(object? sender, object e)
    {
        UpdateNavButtons();
    }

    private async void Core_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // Bloquear launchers o juegos/secciones no permitidos también al abrir en nueva pestaña/ventana
        if (!string.IsNullOrWhiteSpace(e.Uri))
        {
            var check = NavigationGuard.Evaluate(e.Uri, isSubFrame: false, GetAllCurrentOffers());
            if (!check.IsAllowed)
            {
                e.Handled = true;
                if (!string.IsNullOrWhiteSpace(check.BlockReason))
                {
                    ShowBlockedBanner(check.BlockReason);
                }
                return;
            }
        }

        if (_webViewEnvironment is null)
        {
            e.Handled = true;
            return;
        }

        // Mantener popups de login (Google/Xbox/Steam Guard/3DS) dentro de una ventana hija
        // compartiendo exactamente el mismo perfil WebView2, sin abrir Edge externo.
        var deferral = e.GetDeferral();
        try
        {
            var popup = new PopupWebViewWindow(
                msg => Dispatcher.Invoke(() => ShowBlockedBanner(msg)),
                () => Dispatcher.Invoke(() => GetAllCurrentOffers().ToList()))
            {
                Owner = this
            };
            popup.Show();
            await popup.InitializeAsync(_webViewEnvironment);
            e.NewWindow = popup.PopupBrowser.CoreWebView2;
            e.Handled = true;
        }
        catch
        {
            e.Handled = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task NavigateToHttpsUrlAsync(string url)
    {
        var check = NavigationGuard.Evaluate(url, isSubFrame: false, GetAllCurrentOffers());
        if (!check.IsAllowed)
        {
            if (!string.IsNullOrWhiteSpace(check.BlockReason))
            {
                ShowBlockedBanner(check.BlockReason);
            }
            return;
        }

        if (_webViewInitTask is not null)
        {
            await _webViewInitTask;
        }

        if (Browser.CoreWebView2 is null)
        {
            return;
        }

        _lastAllowedUrl = url;
        WelcomeOverlay.Visibility = Visibility.Collapsed;
        Browser.Visibility = Visibility.Visible;
        Browser.CoreWebView2.Navigate(url);
    }

    private async Task RefreshOffersAsync(bool isManual)
    {
        if (isManual)
        {
            var elapsed = DateTimeOffset.UtcNow - _lastManualRefreshUtc;
            if (elapsed < TimeSpan.FromSeconds(5))
            {
                return;
            }
            _lastManualRefreshUtc = DateTimeOffset.UtcNow;
        }

        _refreshCts?.Cancel();
        _refreshCts = new CancellationTokenSource();
        var ct = _refreshCts.Token;

        RefreshButton.IsEnabled = false;
        RefreshButton.Content = "Consultando...";
        WarningText.Visibility = Visibility.Collapsed;

        try
        {
            var result = await _aggregator.FetchLiveOffersAsync(_locale, _userState, ct);

            // Si fallaron todas las tiendas y ya hay ofertas vigentes de caché en pantalla,
            // conservar la lista mostrada y exhibir únicamente el aviso de error de conexión.
            if (result.Warnings.Count >= FreeGamesAggregatorService.StoreCount &&
                result.Offers.Count == 0 &&
                HasDisplayedOffers())
            {
                WarningText.Text = string.Join(" · ", result.Warnings);
                WarningText.Visibility = Visibility.Visible;
            }
            else
            {
                ApplyOffersToUi(result);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelado por cierre o nueva actualización
        }
        catch (Exception ex)
        {
            WarningText.Text = $"Error al consultar ofertas: {ex.Message}";
            WarningText.Visibility = Visibility.Visible;
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            RefreshButton.Content = "↻ Actualizar";
        }
    }

    private void ApplyOffersToUi(OffersRefreshResult result)
    {
        _epicOffers.Clear();
        _steamOffers.Clear();
        _gogOffers.Clear();

        foreach (var offer in result.Offers)
        {
            switch (offer.Platform)
            {
                case StorePlatform.Epic:
                    _epicOffers.Add(offer);
                    break;
                case StorePlatform.Steam:
                    _steamOffers.Add(offer);
                    break;
                case StorePlatform.Gog:
                    _gogOffers.Add(offer);
                    break;
            }
        }

        EpicCountText.Text = $"({_epicOffers.Count})";
        SteamCountText.Text = $"({_steamOffers.Count})";
        GogCountText.Text = $"({_gogOffers.Count})";

        EpicEmptyBox.Visibility = _epicOffers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SteamEmptyBox.Visibility = _steamOffers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        GogEmptyBox.Visibility = _gogOffers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var localTime = result.FetchedAtUtc.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentUICulture);
        var sourceLabel = result.FromCache ? "caché local" : "en vivo";
        LastUpdatedText.Text = $"Actualizado: {localTime} ({sourceLabel})";

        if (result.Warnings.Count > 0)
        {
            WarningText.Text = string.Join(" · ", result.Warnings);
            WarningText.Visibility = Visibility.Visible;
        }
        else
        {
            WarningText.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateNavButtons()
    {
        var core = Browser.CoreWebView2;
        BackButton.IsEnabled = core?.CanGoBack == true;
        ForwardButton.IsEnabled = core?.CanGoForward == true;
        ReloadButton.IsEnabled = core is not null && Browser.Visibility == Visibility.Visible;
    }

    private void ShowBlockedBanner(string message)
    {
        BlockedBannerText.Text = message;
        BlockedBanner.Visibility = Visibility.Visible;
        _bannerHideTimer.Stop();
        _bannerHideTimer.Start();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshOffersAsync(isManual: true);
    }

    private async void OpenEpicSession_Click(object sender, RoutedEventArgs e)
    {
        await NavigateToHttpsUrlAsync(_locale.EpicLoginUrl);
    }

    private async void OpenSteamSession_Click(object sender, RoutedEventArgs e)
    {
        await NavigateToHttpsUrlAsync(_locale.SteamLoginUrl);
    }

    private async void OpenGogSession_Click(object sender, RoutedEventArgs e)
    {
        await NavigateToHttpsUrlAsync(_locale.GogLoginUrl);
    }

    private async void GameCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Si el clic vino desde el botón "Reclamado", no interferir
        if (e.OriginalSource is DependencyObject source && FindParent<Button>(source) is not null)
        {
            return;
        }

        if (sender is FrameworkElement { DataContext: FreeGameOffer offer })
        {
            await NavigateToHttpsUrlAsync(offer.StoreUrl);
        }
    }

    private void ToggleClaimedButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: FreeGameOffer offer })
        {
            offer.IsClaimed = !offer.IsClaimed;

            if (offer.IsClaimed)
            {
                _userState.ClaimedOffers[offer.Id] = DateTimeOffset.UtcNow;
            }
            else
            {
                _userState.ClaimedOffers.Remove(offer.Id);
            }

            _storage.SaveState(_userState);
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2?.CanGoBack == true)
        {
            Browser.CoreWebView2.GoBack();
        }
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2?.CanGoForward == true)
        {
            Browser.CoreWebView2.GoForward();
        }
    }

    private void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        Browser.CoreWebView2?.Reload();
    }

    private void DismissBanner_Click(object sender, RoutedEventArgs e)
    {
        _bannerHideTimer.Stop();
        BlockedBanner.Visibility = Visibility.Collapsed;
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match) return match;

            child = child is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(child)
                : LogicalTreeHelper.GetParent(child);
        }
        return null;
    }

    protected override void OnClosed(EventArgs e)
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _bannerHideTimer.Stop();
        Browser.Dispose();
        _aggregator.Dispose();
        base.OnClosed(e);
    }
}
