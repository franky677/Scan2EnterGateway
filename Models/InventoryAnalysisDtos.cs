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

    public decimal Quantity { get; set; }

    public DateTime? LastSaleDate { get; set; }

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
}