using System.Diagnostics;
using System.Reflection;
using System.Threading;
using Scan2EnterGateway;
using Scan2EnterGateway.Data;


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
        "/api/product/{barcode}/image",
        "PUT /api/product/{articleId}/stock",
        "/api/locations",
        "/api/product/{articleId}/locations",
        "POST /api/product/{articleId}/locations/{locationId}",
        "DELETE /api/product/{articleId}/locations/{locationId}",
        "POST /api/locations",
        "PUT /api/locations/{locationId}",
        "POST /api/locations/{locationId}/duplicate-next",
        "DELETE /api/locations/{locationId}"
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

app.Run();

sealed record StockSettingsRequest(
    int WarehouseId = 0,
    int Variant1Id = -1,
    int Variant2Id = -1,
    int Variant3Id = -1,
    decimal? MinimumStock = null,
    decimal? MaximumStock = null,
    decimal? ReorderLot = null);

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