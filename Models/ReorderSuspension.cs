namespace Scan2EnterGateway.Models;

public sealed record ReorderSuspension
{
    public int IdArticle { get; init; }

    public string ArticleCode { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Barcode { get; init; }

    public int WarehouseId { get; init; }

    public DateTime SuspendedUntil { get; init; }
}
