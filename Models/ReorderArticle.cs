namespace Scan2EnterGateway.Models;

public sealed record ReorderArticle
{
    public int IdArticle { get; init; }
    public string ArticleCode { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Barcode { get; init; }
    public int WarehouseId { get; init; }
    public decimal? Stock { get; init; }
    public decimal? Ordered { get; init; }
    public decimal? Committed { get; init; }
    public decimal? Available { get; init; }
    public decimal MinimumStock { get; init; }
    public decimal? MaximumStock { get; init; }
    public decimal? ReorderLot { get; init; }
    public bool? NotOrderableFromSupplier { get; init; }
    public decimal? SuggestedQuantity => ReorderLot is > 0 ? ReorderLot : null;
}
