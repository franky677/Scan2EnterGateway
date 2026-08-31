using System.Diagnostics;
using System.Reflection;
using System.Threading;
using Scan2EnterGateway;
using Scan2EnterGateway.Data;
using Scan2EnterGateway.Models;


var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "Scan2Enter Gateway";
});

var contentRoot = AppContext.BaseDirectory;
var logDirectory = Path.Combine(contentRoot, "Logs");
// builder.Logging.AddProvider(new GatewayFileLoggerProvider(logDirectory));

builder.Services.AddSingleton<ReorderRepository>();
builder.Services.AddSingleton<ProductRepository>();
builder.Services.AddSingleton<LocationRepository>();
builder.Services.AddSingleton<ProductImageRepository>();
builder.Services.AddSingleton<SessionRepository>();
builder.Services.AddSingleton<ClientPriceRepository>();
builder.Services.AddSingleton<CustomerRepository>();
builder.Services.AddSingleton<ColloRepository>();
builder.Services.AddSingleton<FavoriteRepository>();
builder.Services.AddSingleton<SalesRepository>();
builder.Services.AddSingleton<InventoryAnalysisRepository>();
builder.Services.AddSingleton<ProductExpiryRepository>();
builder.Services.AddSingleton<LabelBitmapRenderer>();
builder.Services.AddSingleton<WindowsLabelPrinter>();
builder.Services.AddSingleton<GodexLabelPrinter>();
builder.Services.AddSingleton<GatewayRuntimeInfo>();

builder.Services.AddCors(o => o.AddPolicy("Scan2Enter", p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("GatewayLifecycle");
var runtimeInfo = app.Services.GetRequiredService<GatewayRuntimeInfo>();

app.Lifetime.ApplicationStarted.Register(() =>
    startupLogger.LogInformation(
        "Gateway avviato. Versione {Version}. PID {ProcessId}. URL http://0.0.0.0:5055",
        runtimeInfo.Version,
        Environment.ProcessId));

app.Lifetime.ApplicationStopping.Register(() =>
    startupLogger.LogInformation("Arresto Gateway richiesto dopo {Uptime}.", runtimeInfo.GetUptime()));

app.Lifetime.ApplicationStopped.Register(() =>
    startupLogger.LogInformation("Gateway arrestato."));

app.UseCors("Scan2Enter");
app.UseDefaultFiles();

app.UseStaticFiles();
app.Use(async (context, next) =>
{
    var requestRuntimeInfo = context.RequestServices.GetRequiredService<GatewayRuntimeInfo>();
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("GatewayRequests");
    var stopwatch = Stopwatch.StartNew();
    var statusCode = 500;

    requestRuntimeInfo.RequestStarted();

    try
    {
        await next();
        statusCode = context.Response.StatusCode;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Errore non gestito durante {Method} {Path}", context.Request.Method, context.Request.Path);
        throw;
    }
    finally
    {
        stopwatch.Stop();
        requestRuntimeInfo.RequestCompleted(stopwatch.Elapsed, statusCode, context.Request.Path);
        logger.LogInformation(
            "{Method} {Path} -> {StatusCode} in {ElapsedMs} ms",
            context.Request.Method,
            context.Request.Path,
            statusCode,
            stopwatch.Elapsed.TotalMilliseconds);
    }
});

app.MapGet("/", () => Results.Ok(new
{
    service = "Scan2Enter Gateway",
    status = "running",
    endpoints = new[]
    {
        "/status",
        "/api/health/database",
        "/api/reorder-list",
        "/api/product/{barcode}",
        "/api/product/{barcode}/health",
        "/api/product/{articleId}/expiry",
        "/api/product-expiry/alerts?months=3",
        "/api/product/{articleId}/price-lists",
        "PUT /api/product/{articleId}/price-lists/{priceListId}",
        "/api/search",
        "/api/session/history",
        "/api/session/customers",
        "/api/session/client-price",
        "POST /api/session/colli",
        "/api/session/colli?days=30&q=",
        "/api/session/colli/{testataId}",
        "/api/product/{barcode}/image",
        "/api/sales/summary?year=2026",
        "/api/inventory-analysis/summary",
        "/api/inventory-analysis/suppliers",
        "/api/inventory-analysis/manufacturers",
        "/api/inventory-analysis/families",
        "/api/inventory-analysis/subfamilies",
        "/api/inventory-analysis/categories",
        "/api/inventory-analysis/subcategories",
        "/api/inventory-analysis/items",
        "/api/inventory-analysis/query",
        "/api/inventory-analysis/classifications",
        "POST /api/inventory-analysis/report",
        "/api/favorites",
        "POST /api/favorites",
        "DELETE /api/favorites/{articleId}",
        "PUT /api/product/{articleId}/stock",
        "PUT /api/product/{articleId}/active",
        "/api/locations",
        "/api/product/{articleId}/locations",
        "POST /api/product/{articleId}/locations/{locationId}",
        "DELETE /api/product/{articleId}/locations/{locationId}",
        "POST /api/locations",
        "PUT /api/locations/{locationId}",
        "POST /api/locations/{locationId}/duplicate-next",
        "DELETE /api/locations/{locationId}",
        "POST /api/labels/print"
    }
}));

app.MapGet("/status", async (
    GatewayRuntimeInfo statusRuntimeInfo,
    ReorderRepository repository,
    CancellationToken ct) =>
{
    var databaseStatus = "connected";
    string? database = null;
    string? databaseError = null;
    var databaseCheck = Stopwatch.StartNew();

    try
    {
        database = await repository.CheckConnectionAsync(ct);
    }
    catch (Exception ex)
    {
        databaseStatus = "disconnected";
        databaseError = ex.Message;
    }
    finally
    {
        databaseCheck.Stop();
    }

    var process = Process.GetCurrentProcess();

    return Results.Ok(new
    {
        service = "Scan2Enter Gateway",
        version = statusRuntimeInfo.Version,
        status = "running",
        startedAt = statusRuntimeInfo.StartedAt,
        uptime = statusRuntimeInfo.GetUptime(),
        uptimeSeconds = (long)statusRuntimeInfo.Uptime.TotalSeconds,
        processId = Environment.ProcessId,
        memoryMb = Math.Round(process.WorkingSet64 / 1024d / 1024d, 1),
        requests = statusRuntimeInfo.RequestCount,
        failedRequests = statusRuntimeInfo.FailedRequestCount,
        activeRequests = statusRuntimeInfo.ActiveRequestCount,
        averageResponseMs = Math.Round(statusRuntimeInfo.AverageResponseMilliseconds, 1),
        lastRequestAt = statusRuntimeInfo.LastRequestAt,
        lastRequestPath = statusRuntimeInfo.LastRequestPath,
        databaseStatus,
        database,
        databaseCheckMs = Math.Round(databaseCheck.Elapsed.TotalMilliseconds, 1),
        databaseError,
        logDirectory
    });
});

app.MapGet("/api/health/database", async (
    ReorderRepository repository,
    CancellationToken ct) =>
{
    try
    {
        var database = await repository.CheckConnectionAsync(ct);

        return Results.Ok(new
        {
            status = "ok",
            database
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Database connection failed",
            detail: ex.Message,
            statusCode: 503);
    }
});

app.MapGet("/api/reorder-list", async (
    ReorderRepository repository,
    CancellationToken ct) =>
{
    try
    {
        var items = await repository.GetReorderListAsync(ct);

        return Results.Ok(new
        {
            count = items.Count,
            generatedAt = DateTimeOffset.Now,
            items
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Unable to read reorder list",
            detail: ex.Message,
            statusCode: 500);
    }
});

app.MapGet("/api/public/product/{barcode}", async (
    string barcode,
    ProductRepository repository,
    CancellationToken ct) =>
{
    try
    {
        var product = await repository.GetByBarcodeAsync(
            barcode,
            ct);

        if (product is null)
        {
            return Results.NotFound(new
            {
                found = false
            });
        }

        return Results.Ok(new
        {
            found = true,
            description = product.Description,
            price = product.PublicPrice
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Unable to read public product",
            detail: ex.Message,
            statusCode: 500);
    }
});

app.MapGet("/api/product/{barcode}", async (
    string barcode,
    ProductRepository repository,
    CancellationToken ct) =>
{
    try
    {
        var product = await repository.GetByBarcodeAsync(barcode, ct);

        if (product is null)
        {
            return Results.NotFound(new
            {
                message = $"Barcode '{barcode}' non trovato."
            });
        }

        return Results.Ok(product);
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Unable to read product",
            detail: ex.Message,
            statusCode: 500);
    }
});


app.MapGet("/api/product/{barcode}/health", async (
    string barcode,
    ProductRepository repository,
    CancellationToken ct) =>
{
    try
    {
        var result =
            await repository.GetHealthByBarcodeAsync(
                barcode,
                ct);

        if (result is null)
        {
            return Results.NotFound(new
            {
                message = $"Salute articolo non disponibile per barcode '{barcode}'."
            });
        }

        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Errore lettura salute articolo",
            detail: ex.Message,
            statusCode: 500);
    }
});


app.MapGet("/api/product/{articleId:long}/price-lists", async (
    long articleId,
    ProductRepository repository,
    CancellationToken ct) =>
{
    try
    {
        if (articleId <= 0)
            return Results.BadRequest(new { message = "Id articolo non valido." });

        var items = await repository.GetPriceListsAsync(articleId, ct);
        if (items.Count == 0)
            return Results.NotFound(new { articleId, message = "Nessun listino vendita trovato." });

        return Results.Ok(new { articleId, count = items.Count, items });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Errore lettura listini vendita",
            detail: ex.Message,
            statusCode: 500);
    }
});


app.MapPut(
    "/api/product/{articleId:long}/price-lists/{priceListId:int}",
    async (
        long articleId,
        int priceListId,
        ProductPriceUpdateRequest request,
        ProductRepository repository,
        CancellationToken ct) =>
    {
        try
        {
            if (articleId <= 0)
            {
                return Results.BadRequest(new
                {
                    updated = false,
                    message = "Id articolo non valido."
                });
            }

            if (priceListId is not (1 or 2 or 3 or 4 or 6))
            {
                return Results.BadRequest(new
                {
                    updated = false,
                    message = "Listino vendita non valido."
                });
            }

            if (request.Price < 0m)
            {
                return Results.BadRequest(new
                {
                    updated = false,
                    message = "Il prezzo non può essere negativo."
                });
            }

            var updated =
                await repository.UpdatePriceListPriceAsync(
                    articleId,
                    priceListId,
                    request.Price,
                    ct);

            if (!updated)
            {
                return Results.NotFound(new
                {
                    updated = false,
                    articleId,
                    priceListId,
                    message = "Prezzo listino non trovato."
                });
            }

            var priceLists =
                await repository.GetPriceListsAsync(
                    articleId,
                    ct);

            var updatedPrice =
                priceLists.FirstOrDefault(
                    x => x.PriceListId == priceListId);

            return Results.Ok(new
            {
                updated = true,
                articleId,
                priceListId,
                price = updatedPrice
            });
        }
        catch (Exception ex)
        {
            return Results.Problem(
                title: "Errore modifica prezzo listino",
                detail: ex.Message,
                statusCode: 500);
        }
    });


app.MapGet("/api/search", async (
    string q,
    ProductRepository repository,
    CancellationToken ct) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return Results.BadRequest(new
            {
                message = "Specificare almeno un testo da cercare."
            });
        }

        var results = await repository.SearchAsync(q, ct);

        return Results.Ok(new
        {
            query = q.Trim(),
            count = results.Count,
            items = results
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Errore ricerca articoli",
            detail: ex.Message,
            statusCode: 500);
    }
});


app.MapGet("/api/session/history", async (
    int clientId,
    string q,
    SessionRepository repository,
    CancellationToken ct) =>
{
    try
    {
        if (clientId <= 0)
        {
            return Results.BadRequest(new
            {
                message = "Cliente non valido."
            });
        }

        if (string.IsNullOrWhiteSpace(q))
        {
            return Results.BadRequest(new
            {
                message = "Specificare articolo, codice o barcode."
            });
        }

        var items = await repository.SearchHistoryAsync(clientId, q, ct);

        return Results.Ok(new
        {
            clientId,
            query = q.Trim(),
            count = items.Count,
            items
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Errore ricerca storico Sessione",
            detail: ex.Message,
            statusCode: 500);
    }
});


app.MapGet("/api/session/customers", async (
    string? q,
    CustomerRepository repository,
    CancellationToken ct) =>
{
    try
    {
        var customers = await repository.SearchAsync(
            q ?? "",
            ct);

        return Results.Ok(new
        {
            query = (q ?? "").Trim(),
            count = customers.Count,
            items = customers
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Errore ricerca clienti Sessione",
            detail: ex.Message,
            statusCode: 500);
    }
});


app.MapGet("/api/session/client-price", async (
    int clientId,
    string barcode,
    ClientPriceRepository repository,
    CancellationToken ct) =>
{
    try
    {
        if (clientId <= 0)
        {
            return Results.BadRequest(new
            {
                message = "Cliente non valido."
            });
        }

        if (string.IsNullOrWhiteSpace(barcode))
        {
            return Results.BadRequest(new
            {
                message = "Barcode non valido."
            });
        }

        var result = await repository.GetAsync(
            clientId,
            barcode,
            ct);

        if (result is null)
        {
            return Results.NotFound(new
            {
                message = "Prezzo cliente non trovato."
            });
        }

        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Errore lettura prezzo cliente",
            detail: ex.Message,
            statusCode: 500);
    }
});


app.MapGet("/api/session/colli", async (
    int? days,
    int? limit,
    string? q,
    ColloRepository repository,
    CancellationToken ct) =>
{
    try
    {
        var results = await repository.SearchHistoryAsync(q, days ?? 30, limit ?? 100, ct);
        return Results.Ok(new { days = days ?? 30, query = (q ?? string.Empty).Trim(), count = results.Count, items = results });
    }
    catch (Exception ex)
    {
        return Results.Problem(title: "Errore storico colli", detail: ex.Message, statusCode: 500);
    }
});

app.MapGet("/api/session/colli/{testataId:int}", async (
    int testataId,
    ColloRepository repository,
    CancellationToken ct) =>
{
    try
    {
        var result = await repository.GetHistoryDetailAsync(testataId, ct);
        if (result is null)
        {
            return Results.NotFound(new { message = "Collo non trovato." });
        }

        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        return Results.Problem(title: "Errore dettaglio collo", detail: ex.Message, statusCode: 500);
    }
});

app.MapPost("/api/session/colli", async (
    CreateColloRequest request,
    ColloRepository repository,
    CancellationToken ct) =>
{
    try
    {
        if (request.ClientId <= 0)
        {
            return Results.BadRequest(new
            {
                created = false,
                message = "Selezionare un cliente."
            });
        }

        if ((request.Note?.Length ?? 0) > 4000)
        {
            return Results.BadRequest(new
            {
                created = false,
                message = "La nota collo supera 4000 caratteri."
            });
        }

        if (request.Items is null || request.Items.Count == 0)
        {
            return Results.BadRequest(new
            {
                created = false,
                message = "La sessione non contiene articoli."
            });
        }

        if (request.Items.Any(x =>
            string.IsNullOrWhiteSpace(x.Barcode) ||
            x.Quantity <= 0 ||
            x.Price < 0))
        {
            return Results.BadRequest(new
            {
                created = false,
                message = "Barcode, quantità o prezzo non validi."
            });
        }

        var created = await repository.CreateAsync(request, ct);

        return Results.Ok(new
        {
            created = true,
            collo = created
        });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new
        {
            created = false,
            message = ex.Message
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Errore creazione collo Sessione",
            detail: ex.Message,
            statusCode: 500);
    }
});



// SCADENZE PRODOTTI SCAN2ENTER.
// Dato proprietario Scan2Enter, separato dalle tabelle funzionali di Due Retail.
app.MapGet("/api/product/{articleId:long}/expiry", async (
    long articleId,
    ProductExpiryRepository repository,
    CancellationToken ct) =>
{
    try
    {
        if (articleId <= 0)
        {
            return Results.BadRequest(new { message = "Id articolo non valido." });
        }

        var expiry = await repository.GetAsync(articleId, ct);

        if (expiry is null)
        {
            return Results.NotFound(new
            {
                articleId,
                hasExpiry = false,
                message = "Nessuna scadenza registrata."
            });
        }

        return Results.Ok(expiry);
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Errore lettura scadenza articolo",
            detail: ex.Message,
            statusCode: 500);
    }
});


app.MapPut("/api/product/{articleId:long}/expiry", async (
    long articleId,
    ProductExpiryUpdateRequest request,
    ProductExpiryRepository repository,
    CancellationToken ct) =>
{
    try
    {
        if (articleId <= 0)
        {
            return Results.BadRequest(new { message = "Id articolo non valido." });
        }

        if (request.Month is < 1 or > 12)
        {
            return Results.BadRequest(new { message = "Il mese deve essere compreso tra 1 e 12." });
        }

        if (request.Year is < 2000 or > 2200)
        {
            return Results.BadRequest(new { message = "Anno scadenza non valido." });
        }

        var result = await repository.UpsertAsync(
            articleId,
            request.Month,
            request.Year,
            ct);

        if (result is null)
        {
            return Results.NotFound(new
            {
                articleId,
                message = "Articolo Due Retail non trovato."
            });
        }

        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Errore salvataggio scadenza articolo",
            detail: ex.Message,
            statusCode: 500);
    }
});


app.MapDelete("/api/product/{articleId:long}/expiry", async (
    long articleId,
    ProductExpiryRepository repository,
    CancellationToken ct) =>
{
    try
    {
        if (articleId <= 0)
        {
            return Results.BadRequest(new { message = "Id articolo non valido." });
        }

        var removed = await repository.DeleteAsync(articleId, ct);

        return Results.Ok(new
        {
            articleId,
            removed
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Errore rimozione scadenza articolo",
            detail: ex.Message,
            statusCode: 500);
    }
});


app.MapGet("/api/product-expiry/alerts", async (
    int? months,
    ProductExpiryRepository repository,
    CancellationToken ct) =>
{
    try
    {
        var selectedMonths = Math.Clamp(months ?? 3, 0, 24);
        var result = await repository.GetAlertsAsync(selectedMonths, ct);
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Errore lettura prodotti in scadenza",
            detail: ex.Message,
            statusCode: 500);
    }
});


app.MapGet("/api/favorites", async (
    FavoriteRepository repository,
    CancellationToken ct) =>
{
    try
    {
        var items = await repository.GetAllAsync(ct);

        return Results.Ok(new
        {
            count = items.Count,
            items
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Errore lettura Preferiti Scan2Enter",
            detail: ex.Message,
            statusCode: 500);
    }
});


app.MapPost("/api/favorites", async (
    FavoriteUpsertRequest request,
    FavoriteRepository repository,
    CancellationToken ct) =>
{
    try
    {
        if (request.ArticleId <= 0)
        {
            return Results.BadRequest(new
            {
                saved = false,
                message = "Id articolo non valido."
            });
        }

        var saved = await repository.UpsertAsync(request, ct);

        return Results.Ok(new
        {
            saved,
            articleId = request.ArticleId
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Errore salvataggio Preferito Scan2Enter",
            detail: ex.Message,
            statusCode: 500);
    }
});


app.MapDelete("/api/favorites/{articleId:long}", async (
    long articleId,
    FavoriteRepository repository,
    CancellationToken ct) =>
{
    try
    {
        if (articleId <= 0)
        {
            return Results.BadRequest(new
            {
                removed = false,
                message = "Id articolo non valido."
            });
        }

        var removed = await repository.RemoveAsync(articleId, ct);

        return Results.Ok(new
        {
            removed,
            articleId
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Errore rimozione Preferito Scan2Enter",
            detail: ex.Message,
            statusCode: 500);
    }
});


app.MapPut("/api/product/{articleId:long}/active", async (
    long articleId,
    ProductActiveRequest request,
    ProductRepository repository,
    CancellationToken ct) =>
{
    try
    {
        if (articleId <= 0)
        {
            return Results.BadRequest(new
            {
                updated = false,
                message = "Id articolo non valido."
            });
        }

        var updated = await repository.UpdateActiveAsync(
            articleId,
            request.Active,
            ct);

        if (!updated)
        {
            return Results.NotFound(new
            {
                updated = false,
                articleId,
                message = "Articolo non trovato."
            });
        }

        return Results.Ok(new
        {
            updated = true,
            articleId,
            active = request.Active,
            message = request.Active
                ? "Articolo sbloccato."
                : "Articolo bloccato."
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Unable to update product active state",
            detail: ex.Message,
            statusCode: 500);
    }
});


app.MapPut("/api/product/{articleId:int}/stock", async (
    int articleId,
    StockSettingsRequest request,
    ReorderRepository repository,
    CancellationToken ct) =>
{
    try
    {
        if (request.WarehouseId < 0)
        {
            return Results.BadRequest(new
            {
                updated = false,
                message = "IdMagazzino non valido."
            });
        }

        await repository.UpdateStockSettingsAsync(
            articleId,
            request.WarehouseId,
            request.Variant1Id,
            request.Variant2Id,
            request.Variant3Id,
            request.MinimumStock,
            request.MaximumStock,
            request.ReorderLot,
            ct);

        return Results.Ok(new
        {
            updated = true,
            articleId,
            warehouseId = request.WarehouseId,
            variant1Id = request.Variant1Id,
            variant2Id = request.Variant2Id,
            variant3Id = request.Variant3Id,
            minimumStock = request.MinimumStock,
            maximumStock = request.MaximumStock,
            reorderLot = request.ReorderLot
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Unable to update stock settings",
            detail: ex.Message,
            statusCode: 500);
    }
});

app.MapGet("/api/product/{barcode}/image", async (
    string barcode,
    ProductRepository productRepository,
    ProductImageRepository imageRepository,
    HttpResponse response,
    CancellationToken ct) =>
{
    try
    {
        // Non memorizzare in cache i risultati senza immagine:
        // appena viene aggiunta una foto, l'app potrà visualizzarla subito.
        response.Headers["Cache-Control"] = "no-store";
        var product = await productRepository.GetByBarcodeAsync(
            barcode,
            ct);

        if (product is null)
        {
            return Results.NotFound(new
            {
                message = $"Barcode '{barcode}' non trovato."
            });
        }

        var imagePath =
            await imageRepository.GetImagePathByArticleIdAsync(
                product.ArticleId,
                ct);

        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return Results.NotFound(new
            {
                message =
                    $"Nessuna immagine trovata per l'articolo {product.ArticleId}.",
                articleId = product.ArticleId,
                barcode
            });
        }

        if (!System.IO.File.Exists(imagePath))
        {
            return Results.NotFound(new
            {
                message =
                    "L'immagine è registrata nel database, ma il file non esiste.",
                articleId = product.ArticleId,
                fileName = Path.GetFileName(imagePath)
            });
        }

        var extension = Path.GetExtension(imagePath).ToLowerInvariant();

        var contentType = extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            _ => "application/octet-stream"
        };

        // Le immagini esistenti possono essere conservate nella cache
        // del dispositivo per 30 giorni.
        response.Headers["Cache-Control"] =
            "public, max-age=2592000";

        return Results.File(
            imagePath,
            contentType: contentType,
            fileDownloadName: null,
            enableRangeProcessing: true);
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Unable to read product image",
            detail: ex.Message,
            statusCode: 500);
    }
});

app.MapGet("/api/locations", async (
    LocationRepository repository,
    CancellationToken ct) =>
{
    try
    {
        var locations = await repository.GetAllAsync(ct);

        return Results.Ok(locations);
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Unable to read locations",
            detail: ex.Message,
            statusCode: 500);
    }
});

app.MapPost("/api/locations", async (
    HttpRequest request,
    LocationRepository repository,
    CancellationToken ct) =>
{
    try
    {
        using var document = await System.Text.Json.JsonDocument.ParseAsync(
            request.Body,
            cancellationToken: ct);

        var name = document.RootElement.TryGetProperty("name", out var value)
            ? value.GetString()?.Trim().ToUpperInvariant()
            : null;

        if (string.IsNullOrWhiteSpace(name))
        {
            return Results.BadRequest(new
            {
                message = "Il nome dell'ubicazione è obbligatorio."
            });
        }

        var location = await repository.CreateLocationAsync(name, ct);

        return Results.Ok(new
        {
            created = true,
            location
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Unable to create location",
            detail: ex.Message,
            statusCode: 500);
    }
});


app.MapPut("/api/locations/{locationId:int}", async (
    int locationId,
    HttpRequest request,
    LocationRepository repository,
    CancellationToken ct) =>
{
    try
    {
        using var document = await System.Text.Json.JsonDocument.ParseAsync(
            request.Body,
            cancellationToken: ct);

        var name = document.RootElement.TryGetProperty("name", out var value)
            ? value.GetString()?.Trim().ToUpperInvariant()
            : null;

        if (string.IsNullOrWhiteSpace(name))
        {
            return Results.BadRequest(new
            {
                message = "Il nome dell'ubicazione è obbligatorio."
            });
        }

        var result = await repository.RenameLocationAsync(locationId, name, ct);

        return result.Status switch
        {
            LocationRenameStatus.NotFound => Results.NotFound(new
            {
                renamed = false,
                message = "Ubicazione non trovata."
            }),

            LocationRenameStatus.Duplicate => Results.Conflict(new
            {
                renamed = false,
                message = "Esiste già un'ubicazione con questo nome.",
                location = result.Location
            }),

            _ => Results.Ok(new
            {
                renamed = true,
                message = "Ubicazione rinominata.",
                location = result.Location
            })
        };
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Unable to rename location",
            detail: ex.Message,
            statusCode: 500);
    }
});

app.MapPost("/api/locations/{locationId:int}/duplicate-next", async (
    int locationId,
    LocationRepository repository,
    CancellationToken ct) =>
{
    try
    {
        var location = await repository.DuplicateNextLocationAsync(locationId, ct);

        if (location is null)
        {
            return Results.NotFound(new
            {
                created = false,
                message = "Ubicazione non trovata."
            });
        }

        return Results.Ok(new
        {
            created = true,
            message = "Ubicazione successiva creata.",
            location
        });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new
        {
            created = false,
            message = ex.Message
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Unable to duplicate next location",
            detail: ex.Message,
            statusCode: 500);
    }
});

app.MapDelete("/api/locations/{locationId:int}", async (
    int locationId,
    LocationRepository repository,
    CancellationToken ct) =>
{
    try
    {
        var usageCount = await repository.GetLocationUsageCountAsync(locationId, ct);

        if (usageCount > 0)
        {
            return Results.Conflict(new
            {
                deleted = false,
                usageCount,
                message = $"Ubicazione utilizzata da {usageCount} articoli."
            });
        }

        var deleted = await repository.DeleteLocationAsync(locationId, ct);

        return Results.Ok(new
        {
            deleted,
            usageCount = 0,
            message = deleted
                ? "Ubicazione eliminata."
                : "Ubicazione non trovata."
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Unable to delete location",
            detail: ex.Message,
            statusCode: 500);
    }
});

app.MapGet("/api/product/{articleId:int}/locations", async (
    int articleId,
    LocationRepository repository,
    CancellationToken ct) =>
{
    try
    {
        var locations = await repository.GetByArticleAsync(articleId, ct);

        return Results.Ok(locations);
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Unable to read article locations",
            detail: ex.Message,
            statusCode: 500);
    }
});

app.MapPost(
    "/api/product/{articleId:int}/locations/{locationId:int}",
    async (
        int articleId,
        int locationId,
        LocationRepository repository,
        CancellationToken ct) =>
    {
        try
        {
            var added = await repository.AddLocationAsync(
                articleId,
                locationId,
                ct);

            var locations = await repository.GetByArticleAsync(
                articleId,
                ct);

            return Results.Ok(new
            {
                articleId,
                locationId,
                added,
                message = added
                    ? "Ubicazione aggiunta."
                    : "Ubicazione già presente.",
                locations
            });
        }
        catch (Exception ex)
        {
            return Results.Problem(
                title: "Unable to add article location",
                detail: ex.Message,
                statusCode: 500);
        }
    });

app.MapDelete(
    "/api/product/{articleId:int}/locations/{locationId:int}",
    async (
        int articleId,
        int locationId,
        LocationRepository repository,
        CancellationToken ct) =>
    {
        try
        {
            var removed = await repository.RemoveLocationAsync(
                articleId,
                locationId,
                ct);

            var locations = await repository.GetByArticleAsync(
                articleId,
                ct);

            return Results.Ok(new
            {
                articleId,
                locationId,
                removed,
                message = removed
                    ? "Ubicazione rimossa."
                    : "Ubicazione non presente.",
                locations
            });
        }
        catch (Exception ex)
        {
            return Results.Problem(
                title: "Unable to remove article location",
                detail: ex.Message,
                statusCode: 500);
        }
    });


app.MapGet("/api/sales/summary", async (
    int? year,
    SalesRepository repository,
    CancellationToken ct) =>
{
    try
    {
        var selectedYear = year ?? DateTime.Today.Year;

        if (selectedYear < 2023 || selectedYear > DateTime.Today.Year)
        {
            return Results.BadRequest(new
            {
                message = $"Anno non valido. Selezionare un anno tra 2023 e {DateTime.Today.Year}."
            });
        }

        var summary = await repository.GetSummaryAsync(
            selectedYear,
            ct);

        return Results.Ok(summary);
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Errore lettura riepilogo vendite",
            detail: ex.Message,
            statusCode: 500);
    }
});


app.MapGet("/api/inventory-analysis/summary", async (
    InventoryAnalysisRepository repository,
    CancellationToken ct) =>
{
    try
    {
        var summary = await repository.GetSummaryAsync(ct);
        return Results.Ok(summary);
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Errore lettura analisi magazzino",
            detail: ex.Message,
            statusCode: 500);
    }
});


app.MapGet("/api/inventory-analysis/suppliers", async (
    InventoryAnalysisRepository repository,
    CancellationToken ct) =>
{
    try
    {
        var items = await repository.GetSupplierSummaryAsync(ct);

        return Results.Ok(new
        {
            count = items.Count,
            generatedAt = DateTimeOffset.Now,
            items
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Errore lettura analisi magazzino per fornitore",
            detail: ex.Message,
            statusCode: 500);
    }
});



app.MapGet("/api/inventory-analysis/manufacturers", async (
    InventoryAnalysisRepository repository,
    CancellationToken ct) =>
{
    try
    {
        var items = await repository.GetManufacturersAsync(ct);
        return Results.Ok(new
        {
            count = items.Count,
            generatedAt = DateTimeOffset.Now,
            items
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Errore lettura analisi magazzino per produttore",
            detail: ex.Message,
            statusCode: 500);
    }
});


app.MapGet("/api/inventory-analysis/families", async (
    InventoryAnalysisRepository repository,
    CancellationToken ct) =>
{
    try
    {
        var items = await repository.GetFamiliesAsync(ct);
        return Results.Ok(new
        {
            count = items.Count,
            generatedAt = DateTimeOffset.Now,
            items
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Errore lettura analisi magazzino per famiglia",
            detail: ex.Message,
            statusCode: 500);
    }
});


app.MapGet("/api/inventory-analysis/subfamilies", async (
    InventoryAnalysisRepository repository,
    CancellationToken ct) =>
{
    try
    {
        var items = await repository.GetSubFamiliesAsync(ct);
        return Results.Ok(new
        {
            count = items.Count,
            generatedAt = DateTimeOffset.Now,
            items
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Errore lettura analisi magazzino per sottofamiglia",
            detail: ex.Message,
            statusCode: 500);
    }
});


app.MapGet("/api/inventory-analysis/categories", async (
    InventoryAnalysisRepository repository,
    CancellationToken ct) =>
{
    try
    {
        var items = await repository.GetCategoriesAsync(ct);
        return Results.Ok(new
        {
            count = items.Count,
            generatedAt = DateTimeOffset.Now,
            items
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Errore lettura analisi magazzino per categoria",
            detail: ex.Message,
            statusCode: 500);
    }
});


app.MapGet("/api/inventory-analysis/subcategories", async (
    InventoryAnalysisRepository repository,
    CancellationToken ct) =>
{
    try
    {
        var items = await repository.GetSubCategoriesAsync(ct);
        return Results.Ok(new
        {
            count = items.Count,
            generatedAt = DateTimeOffset.Now,
            items
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Errore lettura analisi magazzino per sottocategoria",
            detail: ex.Message,
            statusCode: 500);
    }
});



app.MapGet("/api/inventory-analysis/items", async (
    int? rotationId,
    int? supplierId,
    int? manufacturerId,
    int? familyId,
    int? subFamilyId,
    int? categoryId,
    int? subCategoryId,
    string? q,
    int? limit,
    InventoryAnalysisRepository repository,
    CancellationToken ct) =>
{
    try
    {
        if (rotationId is < 1 or > 6)
        {
            return Results.BadRequest(new
            {
                message = "rotationId deve essere compreso tra 1 e 6."
            });
        }

        var filter = new InventoryAnalysisFilterDto
        {
            RotationId = rotationId,
            SupplierId = supplierId,
            ManufacturerId = manufacturerId,
            FamilyId = familyId,
            SubFamilyId = subFamilyId,
            CategoryId = categoryId,
            SubCategoryId = subCategoryId,
            Q = q,
            Limit = Math.Clamp(limit ?? 200, 1, 50000)
        };

        var items = await repository.GetItemsAsync(filter, ct);

        return Results.Ok(new
        {
            count = items.Count,
            generatedAt = DateTimeOffset.Now,
            filter,
            items
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Errore lettura dettaglio analisi magazzino",
            detail: ex.Message,
            statusCode: 500);
    }
});




// INTERROGA MAGAZZINO:
// oltre ai dati standard articolo restituisce anche SoldPeriod, Sold12M,
// SoldHistorical e MonthsCoverage per costruire liste/report analitici.
app.MapGet("/api/inventory-analysis/query", async (
    string mode,
    int? periodMonths,
    int? limit,
    InventoryAnalysisRepository repository,
    CancellationToken ct) =>
{
    try
    {
        var normalizedMode = (mode ?? "").Trim().ToLowerInvariant();

        if (normalizedMode is not ("never-sold" or "top-sold" or "stopped" or "dead-capital" or "growing" or "declining" or "low-stock-fast-moving" or "overstock"))
        {
            return Results.BadRequest(new
            {
                message =
                    "mode deve essere never-sold, top-sold, stopped, dead-capital, " +
                    "growing, declining, low-stock-fast-moving oppure overstock."
            });
        }

        var selectedPeriodMonths = Math.Clamp(periodMonths ?? 12, 1, 120);
        var selectedLimit = Math.Clamp(limit ?? 200, 1, 50000);

        var items = await repository.QueryInventoryAsync(
            normalizedMode,
            selectedPeriodMonths,
            selectedLimit,
            ct);

        var modeTitle = normalizedMode switch
        {
            "never-sold" => "MAI VENDUTI",
            "top-sold" => $"PIÙ VENDUTI - ULTIMI {selectedPeriodMonths} MESI",
            "stopped" => $"FERMI DA ALMENO {selectedPeriodMonths} MESI",
            "dead-capital" => "CAPITALE FERMO",
            "growing" => "IN CRESCITA - 12 MESI VS 12 PRECEDENTI",
            "declining" => "IN CALO - 12 MESI VS 12 PRECEDENTI",
            "low-stock-fast-moving" => "ALTA ROTAZIONE / POCA GIACENZA",
            "overstock" => "SOVRASTOCK",
            _ => normalizedMode
        };

        return Results.Ok(new
        {
            mode = normalizedMode,
            title = modeTitle,
            periodMonths = selectedPeriodMonths,
            count = items.Count,
            generatedAt = DateTimeOffset.Now,
            items
        });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new
        {
            message = ex.Message
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Errore interrogazione Analisi Magazzino",
            detail: ex.Message,
            statusCode: 500);
    }
});


app.MapPost("/api/inventory-analysis/report", async (
    InventoryAnalysisReportRequest request,
    InventoryAnalysisRepository repository,
    CancellationToken ct) =>
{
    try
    {
        var valuation = (request.Valuation ?? "fifo").Trim().ToLowerInvariant();
        if (valuation is not ("fifo" or "purchase"))
        {
            return Results.BadRequest(new
            {
                message = "Valorizzazione non valida. Usare 'fifo' oppure 'purchase'."
            });
        }

        var stockDate = (request.StockDate ?? DateTime.Today).Date;
        if (stockDate > DateTime.Today)
        {
            return Results.BadRequest(new
            {
                message = "La data di valorizzazione non può essere futura."
            });
        }

        var items = await repository.GetReportItemsAsync(request, ct);

        static string H(string? value) =>
            System.Net.WebUtility.HtmlEncode(value ?? "");

        static string N(decimal value) =>
            value.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("it-IT"));

        static string Q(decimal value) =>
            value.ToString("N3", System.Globalization.CultureInfo.GetCultureInfo("it-IT"));

        static string D(DateTime? value) =>
            value.HasValue ? value.Value.ToString("dd/MM/yy") : "-";

        static string Clip(string? value, int max)
        {
            var s = (value ?? "").Trim();
            return s.Length <= max ? s : s[..Math.Max(0, max - 1)] + "…";
        }

        static string Classification(InventoryAnalysisItemDto x)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(x.Family)) parts.Add(x.Family.Trim());
            if (!string.IsNullOrWhiteSpace(x.SubFamily)) parts.Add(x.SubFamily.Trim());
            if (!string.IsNullOrWhiteSpace(x.Category)) parts.Add(x.Category.Trim());
            if (!string.IsNullOrWhiteSpace(x.SubCategory)) parts.Add(x.SubCategory.Trim());
            return string.Join(" / ", parts);
        }

        static string HealthCell(int commercial, int economic)
        {
            var c = Math.Clamp(commercial, 0, 100);
            var e = Math.Clamp(economic, 0, 100);

            return $"""
<div class="health-cell">
  <div class="health-one-line">
    <span class="health-code">C</span>
    <span class="health-track">
      <span class="health-marker" style="left:{c}%"></span>
    </span>
    <span class="health-score">{c}</span>

    <span class="health-code health-code-e">E</span>
    <span class="health-track">
      <span class="health-marker" style="left:{e}%"></span>
    </span>
    <span class="health-score">{e}</span>
  </div>
</div>
""" ;
        }

        var title = string.IsNullOrWhiteSpace(request.Title)
            ? "VALORIZZAZIONE MERCE DI MAGAZZINO"
            : request.Title.Trim();

        var valuationTitle = valuation == "fifo" ? "FIFO" : "LISTINO ACQUISTO";
        var totalQuantity = items.Sum(x => x.Quantity);
        var totalValue = items.Sum(x => valuation == "fifo" ? x.FifoValue : x.PurchaseListValue);

        var extendedReport =
            request.ShowHealthBars ||
            request.ShowLastSale ||
            request.ShowSupplier ||
            request.ShowManufacturer ||
            request.ShowClassification;

        // Una sola riga per articolo: verticale essenziale, orizzontale esteso.
        var rowsPerPage = extendedReport ? 48 : 52;

        var pageCount = Math.Max(
            1,
            (int)Math.Ceiling(items.Count / (double)rowsPerPage));

        var extraHeaders = new System.Text.StringBuilder();
        if (request.ShowLastSale) extraHeaders.Append("""<th class="date-col">Ult. vendita</th>""");
        if (request.ShowSupplier) extraHeaders.Append("""<th class="supplier-col">Fornitore</th>""");
        if (request.ShowManufacturer) extraHeaders.Append("""<th class="manufacturer-col">Produttore</th>""");
        if (request.ShowClassification) extraHeaders.Append("""<th class="classification-col">Classificazione</th>""");
        if (request.ShowHealthBars) extraHeaders.Append("""<th class="health-col">Salute</th>""");

        var options = new List<string>();
        if (request.ShowHealthBars) options.Add("salute");
        if (request.ShowLastSale) options.Add("ultima vendita");
        if (request.ShowSupplier) options.Add("fornitore");
        if (request.ShowManufacturer) options.Add("produttore");
        if (request.ShowClassification) options.Add("classificazione");

        var optionsText = options.Count == 0 ? "report essenziale" : string.Join(", ", options);
        var pageHtml = new System.Text.StringBuilder();

        for (var pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            var pageItems = items
                .Skip(pageIndex * rowsPerPage)
                .Take(rowsPerPage)
                .ToList();

            var rows = string.Join(
                Environment.NewLine,
                pageItems.Select(x =>
                {
                    var rowTotal = valuation == "fifo" ? x.FifoValue : x.PurchaseListValue;
                    var unitCost = x.Quantity == 0m ? 0m : rowTotal / x.Quantity;
                    var extras = new System.Text.StringBuilder();

                    if (request.ShowLastSale)
                        extras.Append($"""<td class="date-col">{D(x.LastSaleDate)}</td>""");

                    if (request.ShowSupplier)
                        extras.Append($"""<td class="supplier-col">{H(Clip(x.Supplier, 28))}</td>""");

                    if (request.ShowManufacturer)
                        extras.Append($"""<td class="manufacturer-col">{H(Clip(x.Manufacturer, 22))}</td>""");

                    if (request.ShowClassification)
                        extras.Append($"""<td class="classification-col">{H(Clip(Classification(x), 34))}</td>""");

                    if (request.ShowHealthBars)
                        extras.Append($"""<td class="health-col">{HealthCell(x.CommercialScore, x.EconomicScore)}</td>""");

                    return $"""
<tr>
  <td class="code-col">{H(x.ArticleCode)}</td>
  <td class="description-col">{H(Clip(x.Description, extendedReport ? 44 : 80))}</td>
  <td class="num qty-col">{Q(x.Quantity)}</td>
  <td class="num cost-col">{N(unitCost)} €</td>
  <td class="num total-col">{N(rowTotal)} €</td>
  {extras}
</tr>
""";
                }));

            var grandTotal = pageIndex == pageCount - 1
                ? $"""
<div class="grand-total">
  <span>TOTALE VALORIZZAZIONE MAGAZZINO</span>
  <strong>{N(totalValue)} €</strong>
</div>
"""
                : "";

            pageHtml.Append(
                $$"""
<section class="report-page">
  <div class="page-header">
    <h1>{{H(title)}}</h1>
    <div class="report-subtitle">Giacenze al {{stockDate:dd/MM/yyyy}}</div>
    <div class="valuation-line">Valorizzazione: <strong>{{valuationTitle}}</strong></div>
    <div class="meta">
      {{items.Count}} articoli · Giacenza {{Q(totalQuantity)}} · Totale {{N(totalValue)}} € ·
      {{H(optionsText)}} · Generato {{DateTime.Now:dd/MM/yyyy HH:mm}}
    </div>
  </div>

  <table>
    <thead>
      <tr>
        <th class="code-col">Codice</th>
        <th class="description-col">Descrizione</th>
        <th class="num qty-col">Giacenza</th>
        <th class="num cost-col">Costo</th>
        <th class="num total-col">Totale</th>
        {{extraHeaders}}
      </tr>
    </thead>
    <tbody>{{rows}}</tbody>
  </table>

  {{grandTotal}}

  <div class="page-footer">Pagina {{pageIndex + 1}} di {{pageCount}}</div>
</section>
""");
        }

        var pageMode = extendedReport ? "A4 landscape" : "A4 portrait";

        var html = $$"""
<!doctype html>
<html lang="it">
<head>
<meta charset="utf-8">
<title>{{H(title)}} - {{valuationTitle}}</title>
<style>
@page { size: {{pageMode}}; margin: 7mm 8mm 8mm 8mm; }
* { box-sizing: border-box; }
html, body { margin: 0; padding: 0; }

body {
  font-family: Arial, Helvetica, sans-serif;
  color: #111;
  font-size: {{(extendedReport ? "7.2pt" : "8.3pt")}};
}

.report-page {
  min-height: {{(extendedReport ? "192mm" : "279mm")}};
  position: relative;
  page-break-after: always;
  break-after: page;
  padding-bottom: 9mm;
}
.report-page:last-child { page-break-after: auto; break-after: auto; }

.page-header { margin-bottom: 1.3mm; }
h1 { font-size: {{(extendedReport ? "12pt" : "14pt")}}; margin: 0 0 .8mm 0; }
.report-subtitle { font-size: {{(extendedReport ? "8.5pt" : "10pt")}}; font-weight: bold; margin-bottom: .5mm; }
.valuation-line { font-size: {{(extendedReport ? "8pt" : "9pt")}}; margin-bottom: 1mm; }
.meta { font-size: {{(extendedReport ? "6.6pt" : "7.5pt")}}; border-top: .4px solid #aaa; padding-top: .6mm; }

table { width: 100%; border-collapse: collapse; table-layout: fixed; }
thead { display: table-header-group; }

th {
  text-align: left;
  border-top: 1px solid #000;
  border-bottom: 1.1px solid #000;
  padding: {{(extendedReport ? ".55mm .45mm" : ".8mm .7mm")}};
  font-size: {{(extendedReport ? "6.5pt" : "7.6pt")}};
}

td {
  border-bottom: .3px solid #bbb;
  padding: {{(extendedReport ? ".32mm .45mm" : ".45mm .7mm")}};
  vertical-align: middle;
  line-height: 1.0;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.num { text-align: right; }
.total-col { font-weight: bold; }

.code-col { width: {{(extendedReport ? "11%" : "19%")}}; }
.description-col { width: {{(extendedReport ? "24%" : "43%")}}; }
.qty-col { width: {{(extendedReport ? "7%" : "11%")}}; }
.cost-col { width: {{(extendedReport ? "8%" : "12%")}}; }
.total-col { width: {{(extendedReport ? "9%" : "15%")}}; }
.date-col { width: 8%; }
.supplier-col { width: 13%; }
.manufacturer-col { width: 10%; }
.classification-col { width: 16%; }
.health-col { width: 14%; }

.health-cell {
  width: 100%;
}

.health-one-line {
  display: flex;
  align-items: center;
  gap: .45mm;
  height: 2mm;
  line-height: 1;
  white-space: nowrap;
}

.health-code {
  width: 2.2mm;
  font-size: 5.6pt;
  font-weight: bold;
}

.health-code-e {
  margin-left: .7mm;
}

.health-track {
  position: relative;
  display: inline-block;
  width: 14mm;
  height: 1.35mm;
  border-radius: .7mm;
  background:
    linear-gradient(
      to right,
      #10a64a 0%,
      #8bc34a 18%,
      #f2d235 34%,
      #e99b24 50%,
      #e76522 64%,
      #c9252d 77%,
      #6d244d 89%,
      #111 100%
    );
}

.health-marker {
  position: absolute;
  top: -.3mm;
  width: .42mm;
  height: 1.95mm;
  background: #fff;
  border: .16mm solid #000;
  transform: translateX(-50%);
}

.health-score {
  width: 4mm;
  text-align: right;
  font-size: 5.6pt;
  font-weight: bold;
}

.grand-total {
  margin-top: 2mm;
  padding: 1.5mm 1mm;
  border-top: 1.2px solid #000;
  border-bottom: 1.2px solid #000;
  display: flex;
  justify-content: space-between;
  align-items: center;
  font-size: 9pt;
}
.grand-total strong { font-size: 10pt; }

.page-footer {
  position: absolute;
  left: 0;
  right: 0;
  bottom: 0;
  text-align: center;
  font-size: 7.5pt;
  font-weight: bold;
  border-top: .4px solid #aaa;
  padding-top: 1.2mm;
}

/* Compattazione mirata solo al report esteso/orizzontale. */
body.extended {
  font-size: 6.9pt;
}

body.extended .report-page {
  min-height: 186mm;
  padding-bottom: 6mm;
}

body.extended .page-header {
  margin-bottom: .7mm;
}

body.extended h1 {
  margin-bottom: .35mm;
}

body.extended .report-subtitle {
  margin-bottom: .25mm;
}

body.extended .valuation-line {
  margin-bottom: .45mm;
}

body.extended .meta {
  padding-top: .3mm;
}

body.extended th {
  padding: .35mm .40mm;
}

body.extended td {
  padding: .16mm .40mm;
}

body.extended .health-one-line {
  height: 1.55mm;
}

body.extended .health-track {
  height: 1.15mm;
}

body.extended .health-marker {
  top: -.25mm;
  height: 1.65mm;
}

@media print {
  tr {
    page-break-inside: avoid;
    break-inside: avoid;
  }
}
</style>
</head>
<body class="{{(extendedReport ? "extended" : "essential")}}">
{{pageHtml}}
</body>
</html>
""";

        return Results.Text(html, "text/html; charset=utf-8");
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Errore generazione report Analisi Magazzino",
            detail: ex.Message,
            statusCode: 500);
    }
});

app.MapGet("/api/inventory-analysis/classifications", async (
    string dimension,
    int? familyId,
    int? subFamilyId,
    int? categoryId,
    int? subCategoryId,
    InventoryAnalysisRepository repository,
    CancellationToken ct) =>
{
    try
    {
        var allowed = new HashSet<string>(
            new[] { "family", "subfamily", "category", "subcategory" },
            StringComparer.OrdinalIgnoreCase);

        if (!allowed.Contains(dimension ?? ""))
        {
            return Results.BadRequest(new
            {
                message =
                    "dimension deve essere family, subfamily, category oppure subcategory."
            });
        }

        var filter = new InventoryAnalysisFilterDto
        {
            FamilyId = familyId,
            SubFamilyId = subFamilyId,
            CategoryId = categoryId,
            SubCategoryId = subCategoryId
        };

        var items = await repository.GetClassificationSummaryAsync(
            dimension,
            filter,
            ct);

        return Results.Ok(new
        {
            dimension,
            count = items.Count,
            generatedAt = DateTimeOffset.Now,
            filter,
            items
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Errore lettura classificazioni magazzino",
            detail: ex.Message,
            statusCode: 500);
    }
});


app.MapPost("/api/labels/print", async (
    LabelPrintRequest request,
    ProductRepository productRepository,
    ProductImageRepository imageRepository,
    GodexLabelPrinter printer,
    CancellationToken ct) =>
{
    try
    {
        if (
            !string.Equals(
                request.Printer,
                "GODEX",
                StringComparison.OrdinalIgnoreCase)
        )
        {
            return Results.BadRequest(new
            {
                printed = false,
                message = "Per ora è disponibile soltanto la GoDEX G500."
            });
        }

        if (request.Quantity is < 1 or > 100)
        {
            return Results.BadRequest(new
            {
                printed = false,
                message = "La quantità deve essere compresa tra 1 e 100."
            });
        }

        var template =
            LabelGenerator.ParseTemplate(request.Template);

        if (
            template == LabelTemplate.Note &&
            string.IsNullOrWhiteSpace(request.Note)
        )
        {
            return Results.BadRequest(new
            {
                printed = false,
                message = "Impossibile stampare: scrivere un testo."
            });
        }

        var articleCode = request.ArticleCode;
        var description = request.Description;
        var publicPrice = request.PublicPrice;
        string? imagePath = null;

        var product =
            await productRepository.GetByBarcodeAsync(
                request.Barcode,
                ct);

        if (product is not null)
        {
            articleCode = product.ArticleCode
                .Trim()
                .IfBlank(request.ArticleCode);

            description = product.Description
                .Trim()
                .IfBlank(request.Description);

            publicPrice = product.PublicPrice
                .Trim()
                .IfBlank(request.PublicPrice);

            if (LabelGenerator.RequiresImage(template))
            {
                imagePath =
                    await imageRepository
                        .GetImagePathByArticleIdAsync(
                            product.ArticleId,
                            ct);
            }
        }

        var rendered = await printer.PrintAsync(
            new LabelRenderRequest(
                ArticleCode: articleCode,
                Description: description,
                Barcode: request.Barcode,
                PublicPrice: publicPrice,
                ImagePath: imagePath,
                Template: template,
                Note: request.Note),
            request.Quantity,
            ct);

        return Results.Ok(new
        {
            printed = true,
            quantity = request.Quantity,
            requestedTemplate =
                LabelGenerator.ToApiValue(
                    rendered.RequestedTemplate),
            actualTemplate =
                LabelGenerator.ToApiValue(
                    rendered.ActualTemplate),
            imageUsed = rendered.ImageUsed,
            imageFallback =
                rendered.RequestedTemplate !=
                rendered.ActualTemplate
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Errore stampa GoDEX",
            detail: ex.Message,
            statusCode: 500);
    }
});

app.Run();

sealed record ProductPriceUpdateRequest(
    decimal Price);

sealed record ProductActiveRequest(
    bool Active);

sealed record StockSettingsRequest(
    int WarehouseId = 0,
    int Variant1Id = -1,
    int Variant2Id = -1,
    int Variant3Id = -1,
    decimal? MinimumStock = null,
    decimal? MaximumStock = null,
    decimal? ReorderLot = null);

sealed record LabelPrintRequest(
    string ArticleCode,
    string Description,
    string Barcode,
    string PublicPrice = "",
    string Printer = "GODEX",
    string Template = "STANDARD",
    int Quantity = 1,
    string Note = "");

static class StringExtensions
{
    public static string IfBlank(
        this string value,
        string fallback)
    {
        return string.IsNullOrWhiteSpace(value)
            ? fallback
            : value;
    }
}

sealed class GatewayRuntimeInfo
{
    private long _requestCount;
    private long _failedRequestCount;
    private long _activeRequestCount;
    private long _totalResponseTicks;
    private long _completedRequestCount;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly object _lastRequestLock = new();
    private DateTimeOffset? _lastRequestAt;
    private string? _lastRequestPath;

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    public string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

    public long RequestCount => Interlocked.Read(ref _requestCount);
    public long FailedRequestCount => Interlocked.Read(ref _failedRequestCount);
    public long ActiveRequestCount => Interlocked.Read(ref _activeRequestCount);
    public TimeSpan Uptime => _stopwatch.Elapsed;

    public double AverageResponseMilliseconds
    {
        get
        {
            var completed = Interlocked.Read(ref _completedRequestCount);
            if (completed == 0)
            {
                return 0;
            }

            var ticks = Interlocked.Read(ref _totalResponseTicks);
            return TimeSpan.FromTicks(ticks / completed).TotalMilliseconds;
        }
    }

    public DateTimeOffset? LastRequestAt
    {
        get
        {
            lock (_lastRequestLock)
            {
                return _lastRequestAt;
            }
        }
    }

    public string? LastRequestPath
    {
        get
        {
            lock (_lastRequestLock)
            {
                return _lastRequestPath;
            }
        }
    }

    public void RequestStarted()
    {
        Interlocked.Increment(ref _requestCount);
        Interlocked.Increment(ref _activeRequestCount);
    }

    public void RequestCompleted(TimeSpan elapsed, int statusCode, string path)
    {
        Interlocked.Decrement(ref _activeRequestCount);
        Interlocked.Increment(ref _completedRequestCount);
        Interlocked.Add(ref _totalResponseTicks, elapsed.Ticks);

        if (statusCode >= 500)
        {
            Interlocked.Increment(ref _failedRequestCount);
        }

        lock (_lastRequestLock)
        {
            _lastRequestAt = DateTimeOffset.Now;
            _lastRequestPath = path;
        }
    }

    public string GetUptime()
    {
        var uptime = Uptime;
        return $"{(int)uptime.TotalDays:00}.{uptime.Hours:00}:{uptime.Minutes:00}:{uptime.Seconds:00}";
    }
}