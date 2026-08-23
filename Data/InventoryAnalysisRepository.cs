using Microsoft.Data.SqlClient;
using Scan2EnterGateway.Models;

namespace Scan2EnterGateway.Data;

public sealed class InventoryAnalysisRepository
{
    private readonly string _connectionString;

    public InventoryAnalysisRepository(IConfiguration configuration)
    {
        _connectionString =
            configuration.GetConnectionString("DueDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'DueDatabase' is missing.");
    }

    public async Task<InventoryAnalysisSummaryDto> GetSummaryAsync(
        CancellationToken ct)
    {
        const string sql = """
SET NOCOUNT ON;

IF OBJECT_ID('tempdb..#Stock') IS NOT NULL DROP TABLE #Stock;
IF OBJECT_ID('tempdb..#UltimaVendita') IS NOT NULL DROP TABLE #UltimaVendita;
IF OBJECT_ID('tempdb..#FIFO') IS NOT NULL DROP TABLE #FIFO;
IF OBJECT_ID('tempdb..#Prezzi') IS NOT NULL DROP TABLE #Prezzi;
IF OBJECT_ID('tempdb..#AnalisiBase') IS NOT NULL DROP TABLE #AnalisiBase;

SELECT
    g.IdArticolo,
    g.Giacenza
INTO #Stock
FROM dbo.tabGiacenzeStoreView g
INNER JOIN dbo.TabArticoli a
    ON a.IdArticolo = g.IdArticolo
WHERE g.idMagazzinoStore = 0
  AND g.Giacenza > 0
  AND a.Attivo = 1;

SELECT
    d.IdArticolo,
    MAX(t.DataDocumento) AS DataUltimaVendita
INTO #UltimaVendita
FROM dbo.tabDettaglioMagazzino d
INNER JOIN dbo.tabTestateMagazzino t
    ON t.IdTestata = d.IdTestata
INNER JOIN dbo.tabCausali c
    ON c.IdCausale = t.IdCausale
INNER JOIN #Stock s
    ON s.IdArticolo = d.IdArticolo
WHERE d.IdArticolo > 0
  AND c.FlagVendita = 1
  AND d.TipoMovimento = 'S'
GROUP BY d.IdArticolo;

SELECT
    IdArticolo,
    SUM(QtaTotAnalizzata) AS QuantitaFIFO,
    SUM(ValorizzazioneTot) AS ValoreFIFO
INTO #FIFO
FROM due_val.TabProdGiacFIFO
WHERE IdMagazzino = 0
  AND QtaTotAnalizzata > 0
GROUP BY IdArticolo;

SELECT
    af.IdArticolo,
    af.IdFornitore,
    CAST(ISNULL(pa.Imponibile, 0) AS float) AS PrezzoNetto,
    ISNULL(pa.Sconto1, 0) AS S1,
    ISNULL(pa.Sconto2, 0) AS S2,
    ISNULL(pa.Sconto3, 0) AS S3,
    ISNULL(pa.Sconto4, 0) AS S4
INTO #Prezzi
FROM dbo.TabArticoliFornitori af
INNER JOIN dbo.TabPrezziAcquisto pa
    ON pa.IdFornitore = af.IdFornitore
   AND pa.CodiceArticoloFornitore = af.CodiceArticoloFornitore
WHERE af.Predefinito <> 0
  AND ISNULL(pa.IdVariante1, -1) <= 0
  AND ISNULL(pa.IdVariante2, -1) <= 0
  AND ISNULL(pa.IdVariante3, -1) <= 0;

UPDATE #Prezzi
SET PrezzoNetto = PrezzoNetto * (1 - S1 / 100.0);

UPDATE #Prezzi
SET PrezzoNetto = PrezzoNetto * (1 - S2 / 100.0);

UPDATE #Prezzi
SET PrezzoNetto = PrezzoNetto * (1 - S3 / 100.0);

UPDATE #Prezzi
SET PrezzoNetto = PrezzoNetto * (1 - S4 / 100.0);

SELECT
    a.IdArticolo,
    s.Giacenza,
    uv.DataUltimaVendita,
    a.TipoUmAcq,
    a.TipoUmMag,
    a.CoeffConversione,
    ISNULL(p.PrezzoNetto, 0) AS PrezzoNetto,
    ISNULL(f.ValoreFIFO, 0) AS ValoreFIFO
INTO #AnalisiBase
FROM #Stock s
INNER JOIN dbo.TabArticoli a
    ON a.IdArticolo = s.IdArticolo
LEFT JOIN #UltimaVendita uv
    ON uv.IdArticolo = s.IdArticolo
LEFT JOIN #FIFO f
    ON f.IdArticolo = s.IdArticolo
LEFT JOIN #Prezzi p
    ON p.IdArticolo = s.IdArticolo;

SELECT
    COUNT(*) AS Articles,
    SUM(Giacenza) AS Quantity,
    SUM(ValoreFIFO) AS FifoValue,
    SUM(
        Giacenza *
        CASE
            WHEN ISNULL(TipoUmAcq, 1) = ISNULL(TipoUmMag, 1)
                THEN PrezzoNetto
            WHEN ISNULL(CoeffConversione, 0) <= 0
                THEN PrezzoNetto
            WHEN TipoUmAcq = 1
                THEN PrezzoNetto / CoeffConversione
            ELSE
                PrezzoNetto * CoeffConversione
        END
    ) AS PurchaseListValue
FROM #AnalisiBase;

SELECT
    RotationId,
    COUNT(*) AS Articles,
    SUM(Giacenza) AS Quantity,
    SUM(ValoreFIFO) AS FifoValue,
    SUM(PurchaseListValue) AS PurchaseListValue
FROM
(
    SELECT
        Giacenza,
        ValoreFIFO,

        CASE
            WHEN DataUltimaVendita IS NULL THEN 1
            WHEN DataUltimaVendita < DATEADD(YEAR, -5, GETDATE()) THEN 2
            WHEN DataUltimaVendita < DATEADD(YEAR, -2, GETDATE()) THEN 3
            WHEN DataUltimaVendita < DATEADD(YEAR, -1, GETDATE()) THEN 4
            WHEN DataUltimaVendita < DATEADD(MONTH, -6, GETDATE()) THEN 5
            ELSE 6
        END AS RotationId,

        Giacenza *
        CASE
            WHEN ISNULL(TipoUmAcq, 1) = ISNULL(TipoUmMag, 1)
                THEN PrezzoNetto
            WHEN ISNULL(CoeffConversione, 0) <= 0
                THEN PrezzoNetto
            WHEN TipoUmAcq = 1
                THEN PrezzoNetto / CoeffConversione
            ELSE
                PrezzoNetto * CoeffConversione
        END AS PurchaseListValue

    FROM #AnalisiBase
) x
GROUP BY RotationId
ORDER BY RotationId;

SELECT MAX(DataCreaz) AS FifoCalculatedAt
FROM due_val.TabProdGiacFIFO
WHERE IdMagazzino = 0;
""";

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = 120;

        await using var reader = await command.ExecuteReaderAsync(ct);

        var result = new InventoryAnalysisSummaryDto();

        if (await reader.ReadAsync(ct))
        {
            result.Articles = Convert.ToInt32(reader["Articles"]);
            result.Quantity = Number(reader, "Quantity");
            result.FifoValue = Number(reader, "FifoValue");
            result.PurchaseListValue = Number(reader, "PurchaseListValue");
        }

        if (await reader.NextResultAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var rotationId = Convert.ToInt32(reader["RotationId"]);
                var fifoValue = Number(reader, "FifoValue");

                result.Rotation.Add(new InventoryRotationSummaryDto
                {
                    RotationId = rotationId,
                    Rotation = RotationName(rotationId),
                    Articles = Convert.ToInt32(reader["Articles"]),
                    Quantity = Number(reader, "Quantity"),
                    FifoValue = fifoValue,
                    PurchaseListValue = Number(reader, "PurchaseListValue")
                });
            }
        }

        if (await reader.NextResultAsync(ct) &&
            await reader.ReadAsync(ct) &&
            reader["FifoCalculatedAt"] != DBNull.Value)
        {
            result.FifoCalculatedAt =
                Convert.ToDateTime(reader["FifoCalculatedAt"]);
        }

        if (result.FifoValue > 0)
        {
            foreach (var row in result.Rotation)
            {
                row.FifoPercentage =
                    Math.Round(
                        row.FifoValue / result.FifoValue * 100m,
                        2);
            }
        }

        return result;
    }

    private static string RotationName(int id)
    {
        return id switch
        {
            1 => "NESSUNA VENDITA NELLO STORICO",
            2 => "FERMO OLTRE 5 ANNI",
            3 => "FERMO 2-5 ANNI",
            4 => "FERMO 1-2 ANNI",
            5 => "FERMO 6-12 MESI",
            6 => "VENDUTO NEGLI ULTIMI 6 MESI",
            _ => "NON CLASSIFICATO"
        };
    }

    private static decimal Number(
        SqlDataReader reader,
        string name)
    {
        var ordinal = reader.GetOrdinal(name);

        return reader.IsDBNull(ordinal)
            ? 0m
            : Convert.ToDecimal(reader.GetValue(ordinal));
    }
}