namespace Scan2EnterGateway.Models;

public sealed class SalesSummaryDto
{
    public int Year { get; set; }
    public DateTime From { get; set; }
    public DateTime To { get; set; }

    public SalesSummarySectionDto Receipts { get; set; } = new();
    public SalesSummarySectionDto Invoices { get; set; } = new();
    public SalesSummarySectionDto Total { get; set; } = new();
}

public sealed class SalesSummarySectionDto
{
    public int Documents { get; set; }
    public decimal SalesTaxable { get; set; }
    public decimal Cost { get; set; }
    public decimal Difference { get; set; }
    public decimal MarkupPercent { get; set; }
}
