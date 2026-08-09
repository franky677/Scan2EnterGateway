namespace Scan2EnterGateway.Models;

public sealed class SearchResultDto
{
    public long Id { get; set; }

    public string Code { get; set; } = "";

    public string Description { get; set; } = "";

    public string Barcode { get; set; } = "";

    public string Price { get; set; } = "";

    public string Stock { get; set; } = "";

    public bool Moved { get; set; }

    public DateTime? LastMovement { get; set; }
}
