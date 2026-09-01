namespace Scan2EnterGateway.Models;

public sealed record CreateColloRequest(
    int ClientId,
    List<CreateColloItemRequest> Items,
    string? Note);

public sealed record CreateColloItemRequest(
    string Barcode,
    decimal Quantity,
    decimal Price,
    decimal? ListPrice = null,
    decimal Discount1 = 0m,
    decimal Discount2 = 0m,
    decimal Discount3 = 0m,
    decimal Discount4 = 0m,
    decimal ManualDiscount = 0m,
    int? PriceListId = null);

public sealed class CreatedColloDto
{
    public int TestataId { get; set; }
    public string NumeroCollo { get; set; } = "";
    public int ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public string BarcodeCollo { get; set; } = "";
    public int ItemCount { get; set; }
    public decimal Total { get; set; }
    public DateTime CreatedAt { get; set; }
}


public sealed class ColloHistorySummaryDto
{
    public int TestataId { get; set; }
    public string NumeroCollo { get; set; } = "";
    public string BarcodeCollo { get; set; } = "";
    public int ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public int ItemCount { get; set; }
    public decimal PieceCount { get; set; }
    public decimal Total { get; set; }
    public bool IsElaborato { get; set; }
    public bool HasNote { get; set; }
}

public sealed class ColloHistoryItemDto
{
    public long ArticleId { get; set; }
    public string ArticleCode { get; set; } = "";
    public string Description { get; set; } = "";
    public string Barcode { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal Total { get; set; }
}

public sealed class ColloHistoryDetailDto
{
    public int TestataId { get; set; }
    public string NumeroCollo { get; set; } = "";
    public string BarcodeCollo { get; set; } = "";
    public int ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public bool IsElaborato { get; set; }
    public decimal Total { get; set; }
    public string Note { get; set; } = "";
    public List<ColloHistoryItemDto> Items { get; set; } = [];
}
