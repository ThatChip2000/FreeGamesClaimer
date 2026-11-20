# Free Games Claimer (Epic Games, Steam & GOG)

Aplicación de escritorio nativa para Windows (`.exe`) **simple, liviana y de arranque rápido** diseñada exclusivamente para ver y reclamar juegos gratis temporales (*Free to Keep* / *Giveaways*) en **Epic Games**, **Steam** y **GOG** sin abrir ni descargar nunca los launchers de escritorio.

---

## 1. Arquitectura y Stack Implementado

* **Stack:** C# · **.NET 10 (`net10.0-windows`)** · **WPF** · **Microsoft Edge WebView2** (`Microsoft.Web.WebView2`).
* **Soporte Multi-Monitor / DPI:** Manifiesto `PerMonitorV2` (`app.manifest`) con posicionamiento y ajuste automático dentro del área de trabajo del monitor principal (`SystemParameters.WorkArea`).
* **Cierre limpio:** `ShutdownMode="OnMainWindowClose"` — cero procesos residentes en segundo plano al cerrar la ventana.

---

## 2. Seguridad, Privacidad y Cero Riesgo de Baneo

* **Cero automatización en login y checkout:**
  * No se inyecta JavaScript ni CSS en las tiendas, no se manipula el DOM, no se falsifica el `User-Agent` de WebView2 y no se hacen clics ni compras automáticas.
  * El inicio de sesión (2FA / Steam Guard / OAuth / hCaptcha / Cloudflare) y el reclamo se realizan 100% manualmente sobre las páginas HTTPS oficiales.
* **Cero almacenamiento de contraseñas:**
  * `IsPasswordAutosaveEnabled = false` e `IsGeneralAutofillEnabled = false` tanto en la ventana principal como en popups.
  * Las cookies de sesión las gestiona y cifra nativamente **WebView2** mediante **DPAPI** de Windows.
* **Perfil aislado fuera de OneDrive (`Services/AppPaths.cs`):**
  * Todos los datos persistentes se guardan exclusivamente en `%LOCALAPPDATA%\FreeGamesClaimer`:
    * `WebView2Data\` — perfil y cookies de WebView2.
    * `cache.json` — caché local de ofertas públicas (TTL de 30 minutos + invalidación automática al vencer ofertas o cambiar versión de esquema).
    * `state.json` — marca manual local de juegos reclamados.
* **Consultas públicas anónimas:**
  * Los servicios de catálogo usan `HttpClientHandler.UseCookies = false` y se conectan directamente a los dominios oficiales sin servidores intermedios ni telemetría.

---

## 3. Lista Blanca Estricta, Bloqueo de Launchers y Cero Descargas

* **Bloqueo total de descargas (`DownloadStarting`):**
  * Cualquier descarga iniciada desde WebView2 (incluidos los instaladores de Epic Games Launcher, Steam o GOG Galaxy) se cancela automáticamente.
* **Bloqueo de protocolos externos (`NavigationStarting`, `FrameNavigationStarting` y `NewWindowRequested`):**
  * Solo se permite tráfico `https://` (y esquemas internos `about:srcdoc` / `blob:https://` exclusivamente en sub-frames para CAPTCHAs y pasarelas de pago).
  * Se bloquean `steam://`, `epicgames://`, `com.epicgames.launcher://`, `goggalaxy://` y cualquier protocolo no HTTPS tanto en el marco principal como en `iframes` y ventanas emergentes.
* **Lista blanca de navegación (`Services/NavigationGuard.cs`):**
  * En el marco principal (incluyendo navegación SPA del lado cliente vía `SourceChanged` en ventana principal y popups) se permiten únicamente las fichas de los juegos gratis ofertados activos y las rutas necesarias para iniciar sesión, 2FA, OAuth, verificación de edad y checkout/reclamo.
  * Cualquier otro juego, catálogo, página de descarga, soporte o enlace externo queda bloqueado.

---

## 4. Detección de Juegos Gratis (Solo Lectura)

Región e idioma se detectan automáticamente desde Windows (`Services/RegionLocaleProvider.cs`):

* **Epic Games (`Services/EpicFreeGamesService.cs`):** consulta `store-site-backend-static.ak.epicgames.com/freeGamesPromotions`, filtrando únicamente ofertas activas en este momento al 100% de descuento (`discountPrice == 0` y ventana vigente en `promotionalOffers`).
* **Steam (`Services/SteamFreeGamesService.cs`):** consulta `store.steampowered.com/search/results/` con `maxprice=free&specials=1`, exigiendo `data-discount="100"`, `data-price-final="0"` y precio original visible para excluir *Free-to-Play* permanentes.
* **GOG (`Services/GogFreeGamesService.cs`):** consulta `catalog.gog.com/v1/catalog` para juegos con `-100%` y precio base `> 0`, más comprobación ligera de banners de *giveaway* activos en la portada pública de GOG (`#giveaway`).

---

## 5. Compilación y Ejecución

Requisitos: Windows 10/11 (con Microsoft Edge WebView2 Runtime) y `.NET 10 SDK` para compilar.

```powershell
# Compilar en modo Release
dotnet build -c Release

# Ejecutar directamente
dotnet run -c Release
```

El ejecutable compilado se genera en `bin\Release\net10.0-windows\FreeGamesClaimer.exe`.
