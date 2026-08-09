namespace Scan2EnterGateway.Models;

public sealed class SessionHistoryItemDto
{
    public int TestataId { get; set; }
    public int DetailId { get; set; }

    public string NumeroCollo { get; set; } = "";

    public int ClientId { get; set; }
    public string ClientName { get; set; } = "";

    public int ArticleId { get; set; }
    public string ArticleCode { get; set; } = "";
    public string Barcode { get; set; } = "";
    public string Description { get; set; } = "";

    public decimal? Price { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? Total { get; set; }

    public DateTime? Date { get; set; }
}