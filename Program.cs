using Scan2EnterGateway.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ReorderRepository>();
builder.Services.AddSingleton<ProductRepository>();

builder.Services.AddCors(o => o.AddPolicy("Scan2Enter", p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

app.UseCors("Scan2Enter");

app.MapGet("/", () => Results.Ok(new
{
    service = "Scan2Enter Gateway",
    status = "running",
    endpoints = new[]
    {
        "/api/health/database",
        "/api/reorder-list",
        "/api/product/{barcode}"
    }
}));

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

app.Run();