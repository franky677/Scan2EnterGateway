namespace Scan2EnterGateway.Models;

public sealed record ReorderSupplierOption
{
    public long SupplierId { get; init; }
    public string SupplierName { get; init; } = "";
    public string SupplierArticleCode { get; init; } = "";
    public bool IsDefault { get; init; }
    public bool IsSelected { get; init; }
    public decimal? PurchaseTaxable { get; init; }
    public decimal? NetPurchaseTaxable { get; init; }
    public decimal? PurchasePrice { get; init; }
    public decimal? VatRate { get; init; }
    public DateTime? UpdatedAt { get; init; }
}

public sealed record SetReorderSupplierRequest
{
    public int WarehouseId { get; init; } = 0;
    public int SupplierId { get; init; }
}
