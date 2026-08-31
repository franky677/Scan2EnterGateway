namespace Scan2EnterGateway.Models;

public sealed class InventoryAnalysisSummaryDto
{
    public int Articles { get; set; }
    public decimal Quantity { get; set; }
    public decimal FifoValue { get; set; }
    public decimal PurchaseListValue { get; set; }
    public DateTime? FifoCalculatedAt { get; set; }

    public List<InventoryRotationSummaryDto> Rotation { get; set; } = new();
}

public sealed class InventoryRotationSummaryDto
{
    public int RotationId { get; set; }
    public string Rotation { get; set; } = "";

    public int Articles { get; set; }
    public decimal Quantity { get; set; }

    public decimal FifoValue { get; set; }
    public decimal PurchaseListValue { get; set; }

    public decimal FifoPercentage { get; set; }
}

public sealed class InventoryAnalysisItemDto
{
    public int ArticleId { get; set; }

    public string ArticleCode { get; set; } = "";
    public string Description { get; set; } = "";
    public string Barcode { get; set; } = "";

    public decimal Quantity { get; set; }

    public DateTime? LastSaleDate { get; set; }

    // Dati analitici usati da INTERROGA MAGAZZINO.
    // Nei normali elenchi possono restare a zero/null.
    public decimal SoldPeriod { get; set; }
    public decimal Sold12M { get; set; }
    public decimal SoldPrevious12M { get; set; }
    public decimal SoldHistorical { get; set; }
    public decimal? TrendPercent { get; set; }
    public decimal? MonthsCoverage { get; set; }

    public int RotationId { get; set; }
    public string Rotation { get; set; } = "";

    public int? SupplierId { get; set; }
    public string? Supplier { get; set; }

    public int? ManufacturerId { get; set; }
    public string? Manufacturer { get; set; }

    public int? FamilyId { get; set; }
    public string? Family { get; set; }

    public int? SubFamilyId { get; set; }
    public string? SubFamily { get; set; }

    public int? CategoryId { get; set; }
    public string? Category { get; set; }

    public int? SubCategoryId { get; set; }
    public string? SubCategory { get; set; }

    public decimal FifoValue { get; set; }
    public decimal PurchaseListValue { get; set; }

    // Salute Articolo V2 - valori 0..100
    public int CommercialScore { get; set; }
    public int EconomicScore { get; set; }

    public string CommercialDescription { get; set; } = "";
    public string EconomicDescription { get; set; } = "";
}

public sealed class InventorySupplierSummaryDto
{
    public int? SupplierId { get; set; }
    public string Supplier { get; set; } = "";

    public int Articles { get; set; }
    public decimal Quantity { get; set; }

    public decimal FifoValue { get; set; }
    public decimal PurchaseListValue { get; set; }
}

public sealed class InventoryDimensionSummaryDto
{
    public int? Id { get; set; }
    public string Name { get; set; } = "";

    public int Articles { get; set; }
    public decimal Quantity { get; set; }

    public decimal FifoValue { get; set; }
    public decimal PurchaseListValue { get; set; }
}

public sealed class InventoryAnalysisFilterDto
{
    public int? RotationId { get; set; }
    public int? SupplierId { get; set; }
    public int? ManufacturerId { get; set; }
    public int? FamilyId { get; set; }
    public int? SubFamilyId { get; set; }
    public int? CategoryId { get; set; }
    public int? SubCategoryId { get; set; }
    public string? Q { get; set; }
    public int Limit { get; set; } = 200;
}


public sealed class InventoryAnalysisReportRequest
{
    public string Valuation { get; set; } = "fifo";
    public string? Title { get; set; }

    // Predisposto per la futura valorizzazione storica.
    // Per ora sono ammessi null oppure la data odierna.
    public DateTime? StockDate { get; set; }

    public bool ShowHealthBars { get; set; }
    public bool ShowLastSale { get; set; }
    public bool ShowSupplier { get; set; }
    public bool ShowManufacturer { get; set; }
    public bool ShowClassification { get; set; }

    public int? RotationId { get; set; }
    public int? SupplierId { get; set; }
    public int? ManufacturerId { get; set; }
    public int? FamilyId { get; set; }
    public int? SubFamilyId { get; set; }
    public int? CategoryId { get; set; }
    public int? SubCategoryId { get; set; }
    public string? Q { get; set; }

    // Interroga Magazzino: se valorizzato, il report usa esattamente
    // la stessa selezione dell'endpoint /api/inventory-analysis/query.
    public string? QueryMode { get; set; }
    public int? PeriodMonths { get; set; }
}

public sealed class InventoryAnalysisReportDto
{
    public string FileName { get; set; } = "";
    public string FullPath { get; set; } = "";
    public int Articles { get; set; }
    public decimal Quantity { get; set; }
    public decimal TotalValue { get; set; }
}
