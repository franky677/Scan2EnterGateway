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

    // Vendite nei 12 mesi precedenti agli ultimi 12 mesi.
    public decimal VendutoAnnoPrecedente { get; set; }

    // Numero di mesi distinti con almeno una vendita negli ultimi 12 mesi.
    public int MesiConVendite12M { get; set; }

    public decimal Rotazione12M { get; set; }
    public decimal? MesiCopertura { get; set; }

    // Salute V1 - mantenuta per compatibilita' con Android attuale.
    public string StatoSalute { get; set; } = "OK";
    public string DescrizioneSalute { get; set; } = "Regolare";

    // Salute V2.
    // 0 = ottimo / nessuna criticita'
    // 100 = massima criticita'
    public int PunteggioCommerciale { get; set; }
    public int PunteggioEconomico { get; set; }

    public string DescrizioneCommerciale { get; set; } = string.Empty;
    public string DescrizioneEconomica { get; set; } = string.Empty;
}
