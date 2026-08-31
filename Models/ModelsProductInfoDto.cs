namespace Scan2EnterGateway.Models;

public class ProductInfoDto
{
    public long ArticleId { get; set; }

    public string ArticleCode { get; set; } = "";

    public string Description { get; set; } = "";

    public string Barcode { get; set; } = "";

    public bool Active { get; set; } = true;

    public string TaxablePrice { get; set; } = "";

    public string VatRate { get; set; } = "";

    public string PublicPrice { get; set; } = "";

    public string PurchaseTaxable { get; set; } = "";

    public string Season { get; set; } = "";

    public string Year { get; set; } = "";

    public string Location { get; set; } = "";

    public string Stock { get; set; } = "";

    public string AvailableStock { get; set; } = "";

    public string MinimumStock { get; set; } = "";

    public string MaximumStock { get; set; } = "";

    public string ReorderLot { get; set; } = "";

    public long SupplierId { get; set; }

    public string SupplierName { get; set; } = "";

    public string SupplierArticleCode { get; set; } = "";

    public string CoverImagePath { get; set; } = "";
}