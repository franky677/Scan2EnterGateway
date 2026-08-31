namespace Scan2EnterGateway.Models;

public sealed class ProductExpiryUpdateRequest
{
    public int Month { get; set; }
    public int Year { get; set; }
}

public sealed class ProductExpiryDto
{
    public long ArticleId { get; set; }
    public string ArticleCode { get; set; } = "";
    public string Description { get; set; } = "";
    public string Barcode { get; set; } = "";

    public int Month { get; set; }
    public int Year { get; set; }

    // La scadenza mese/anno viene considerata valida fino all'ultimo giorno del mese.
    public DateTime ExpiryDate { get; set; }

    public bool IsExpired { get; set; }
    public int DaysToExpiry { get; set; }

    public decimal Stock { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class ProductExpiryAlertsDto
{
    public int WithinMonths { get; set; }
    public int ExpiredCount { get; set; }
    public int ExpiringCount { get; set; }
    public int TotalCount { get; set; }
    public DateTime GeneratedAt { get; set; }
    public List<ProductExpiryDto> Items { get; set; } = [];
}
