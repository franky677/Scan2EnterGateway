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
            Limit = Math.Clamp(limit ?? 200, 1, 2000)
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