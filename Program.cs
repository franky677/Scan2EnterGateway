using Scan2EnterGateway.Data;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<ReorderRepository>();
builder.Services.AddCors(o => o.AddPolicy("Scan2Enter", p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors("Scan2Enter");

app.MapGet("/", () => Results.Ok(new {
    service = "Scan2Enter Gateway",
    status = "running",
    endpoint = "/api/reorder-list"
}));

app.MapGet("/api/health/database", async (
    ReorderRepository repository,
    CancellationToken ct) =>
{
    try {
        var database = await repository.CheckConnectionAsync(ct);
        return Results.Ok(new { status = "ok", database });
    }
    catch (Exception ex) {
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
    try {
        var items = await repository.GetReorderListAsync(ct);
        return Results.Ok(new {
            count = items.Count,
            generatedAt = DateTimeOffset.Now,
            items
        });
    }
    catch (Exception ex) {
        return Results.Problem(
            title: "Unable to read reorder list",
            detail: ex.Message,
            statusCode: 500);
    }
});

app.Run();
