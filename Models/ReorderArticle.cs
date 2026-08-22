namespace Scan2EnterGateway.Models;

public sealed record ReorderArticle
{
    public int IdArticle { get; init; }

    public string ArticleCode { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Barcode { get; init; }

    public int WarehouseId { get; init; }

    public long SupplierId { get; init; }
    public string SupplierName { get; init; } = "";
    public string SupplierArticleCode { get; init; } = "";

    public decimal? PurchaseTaxable { get; init; }
    public decimal? PurchasePrice { get; init; }
    public decimal? VatRate { get; init; }

    public decimal? Stock { get; init; }
    public decimal? Ordered { get; init; }
    public decimal? Committed { get; init; }
    public decimal? Available { get; init; }

    public decimal? MinimumStock { get; init; }
    public decimal? MaximumStock { get; init; }
    public decimal? ReorderLot { get; init; }

    public bool? NotOrderableFromSupplier { get; init; }

    public decimal? SuggestedQuantity { get; init; }
}