using System.Windows;
using System.Windows.Threading;
using FreeGamesClaimer.Models;
using FreeGamesClaimer.Services;
using Microsoft.Web.WebView2.Core;

namespace FreeGamesClaimer.Windows;

public partial class PopupWebViewWindow : Window
{
    private readonly Action<string>? _onBlockedAction;
    private readonly Func<IEnumerable<FreeGameOffer>>? _getAllowedOffers;
    private CoreWebView2Environment? _environment;
    private string? _lastAllowedUrl;
    private bool _isRevertingBlockedNavigation;

    public PopupWebViewWindow(
        Action<string>? onBlockedAction = null,
        Func<IEnumerable<FreeGameOffer>>? getAllowedOffers = null)
    {
        InitializeComponent();
        _onBlockedAction = onBlockedAction;
        _getAllowedOffers = getAllowedOffers;
        ClampSizeToPrimaryWorkArea();
    }

    private void ClampSizeToPrimaryWorkArea()
    {
        var workArea = SystemParameters.WorkArea;
        if (workArea.Width <= 0 || workArea.Height <= 0) return;

        double maxWidth = Math.Max(360, workArea.Width - 40);
        double maxHeight = Math.Max(380, workArea.Height - 40);

        MinWidth = Math.Min(MinWidth, maxWidth);
        MinHeight = Math.Min(MinHeight, maxHeight);

        Width = Math.Min(520, maxWidth);
        Height = Math.Min(640, maxHeight);
    }

    public async Task InitializeAsync(CoreWebView2Environment environment)
    {
        _environment = environment;
        await PopupBrowser.EnsureCoreWebView2Async(environment);
        var core = PopupBrowser.CoreWebView2;

        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsStatusBarEnabled = false;

        core.DownloadStarting += (_, args) =>
        {
            args.Cancel = true;
            args.Handled = true;
            _onBlockedAction?.Invoke("Descarga bloqueada: esta app no descarga archivos ni instaladores.");
        };

        core.NavigationStarting += (_, args) =>
        {
            var check = NavigationGuard.Evaluate(args.Uri, isSubFrame: false, _getAllowedOffers?.Invoke());
            if (!check.IsAllowed)
            {
                args.Cancel = true;
                if (!string.IsNullOrWhiteSpace(check.BlockReason))
                {
                    _onBlockedAction?.Invoke(check.BlockReason);
                }

                // Si el popup aún no cargó ninguna página válida (ej. window.open('', '_blank') hacia steam://),
                // cerrarlo automáticamente para no dejar una ventana vacía huérfana.
                if (string.IsNullOrWhiteSpace(core.Source) ||
                    string.Equals(core.Source, "about:blank", StringComparison.OrdinalIgnoreCase))
                {
                    Dispatcher.BeginInvoke(Close);
                }
            }
        };

        core.FrameNavigationStarting += (_, args) =>
        {
            var check = NavigationGuard.Evaluate(args.Uri, isSubFrame: true);
            if (!check.IsAllowed)
            {
                args.Cancel = true;
                if (check.IsLauncherScheme && !string.IsNullOrWhiteSpace(check.BlockReason))
                {
                    _onBlockedAction?.Invoke(check.BlockReason);
                }
            }
        };

        core.NewWindowRequested += Core_NewWindowRequested;

        core.SourceChanged += (_, _) =>
        {
            var source = core.Source;
            if (string.IsNullOrWhiteSpace(source))
            {
                return;
            }

            // Protege también contra navegación SPA (history.pushState) dentro de popups
            var allowedOffers = _getAllowedOffers?.Invoke();
            var check = NavigationGuard.Evaluate(source, isSubFrame: false, allowedOffers);
            if (!check.IsAllowed)
            {
                if (!string.IsNullOrWhiteSpace(check.BlockReason))
                {
                    _onBlockedAction?.Invoke(check.BlockReason);
                }

                if (!_isRevertingBlockedNavigation)
                {
                    _isRevertingBlockedNavigation = true;
                    try
                    {
                        // Consumir _lastAllowedUrl al revertir para impedir bucles infinitos si redirige de nuevo
                        var revertTarget = _lastAllowedUrl;
                        _lastAllowedUrl = null;

                        bool canRevertToLast =
                            !string.IsNullOrWhiteSpace(revertTarget) &&
                            !string.Equals(revertTarget, source, StringComparison.OrdinalIgnoreCase) &&
                            NavigationGuard.Evaluate(revertTarget, isSubFrame: false, allowedOffers).IsAllowed;

                        if (canRevertToLast)
                        {
                            core.Navigate(revertTarget!);
                        }
                        else
                        {
                            Dispatcher.BeginInvoke(Close);
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

            UpdateHeader(source);
        };

        core.DocumentTitleChanged += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(core.DocumentTitle))
            {
                Title = core.DocumentTitle;
            }
        };

        core.WindowCloseRequested += (_, _) =>
        {
            Close();
        };
    }

    private async void Core_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.Uri))
        {
            var check = NavigationGuard.Evaluate(e.Uri, isSubFrame: false, _getAllowedOffers?.Invoke());
            if (!check.IsAllowed)
            {
                e.Handled = true;
                if (!string.IsNullOrWhiteSpace(check.BlockReason))
                {
                    _onBlockedAction?.Invoke(check.BlockReason);
                }
                return;
            }
        }

        if (_environment is null)
        {
            e.Handled = true;
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            var nestedPopup = new PopupWebViewWindow(_onBlockedAction, _getAllowedOffers)
            {
                Owner = this
            };
            nestedPopup.Show();
            await nestedPopup.InitializeAsync(_environment);
            e.NewWindow = nestedPopup.PopupBrowser.CoreWebView2;
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

    private void UpdateHeader(string? rawUri)
    {
        if (Uri.TryCreate(rawUri, UriKind.Absolute, out var uri))
        {
            PopupAddressText.Text = uri.Host + uri.AbsolutePath;
        }
        else if (!string.IsNullOrWhiteSpace(rawUri))
        {
            PopupAddressText.Text = rawUri;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        PopupBrowser.Dispose();
        base.OnClosed(e);
    }
}
