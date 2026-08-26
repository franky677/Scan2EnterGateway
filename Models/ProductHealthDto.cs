namespace Scan2EnterGateway.Models;

public sealed class ProductHealthDto
{
    public long IdArticolo { get; set; }
    public string Barcode { get; set; } = string.Empty;

    public decimal GiacenzaFifo { get; set; }
    public decimal ValoreFifo { get; set; }
    public decimal CostoMedioFifo { get; set; }

    public DateTime? UltimaVendita { get; set; }
    public int? GiorniDaUltimaVendita { get; set; }

    public decimal Venduto12M { get; set; }
    public decimal Venduto24M { get; set; }

    public decimal Rotazione12M { get; set; }
    public decimal? MesiCopertura { get; set; }

    public string StatoSalute { get; set; } = "OK";
    public string DescrizioneSalute { get; set; } = "Regolare";
}
