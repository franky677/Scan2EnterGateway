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
builder.Logging.AddProvider(new GatewayFileLoggerProvider(logDirectory));

builder.Services.AddSingleton<ReorderRepository>();
builder.Services.AddSingleton<ProductRepository>();
builder.Services.AddSingleton<LocationRepository>();
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
        "/api/locations",
        "/api/product/{articleId}/locations",
        "POST /api/product/{articleId}/locations/{locationId}",
        "DELETE /api/product/{articleId}/locations/{locationId}"
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
