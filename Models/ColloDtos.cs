namespace Scan2EnterGateway.Models;

public sealed record CreateColloRequest(
    int ClientId,
    List<CreateColloItemRequest> Items);

public sealed record CreateColloItemRequest(
    string Barcode,
    decimal Quantity,
    decimal Price);

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