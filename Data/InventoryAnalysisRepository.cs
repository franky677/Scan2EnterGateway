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


    public async Task<List<InventorySupplierSummaryDto>> GetSupplierSummaryAsync(
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

UPDATE #Prezzi SET PrezzoNetto = PrezzoNetto * (1 - S1 / 100.0);
UPDATE #Prezzi SET PrezzoNetto = PrezzoNetto * (1 - S2 / 100.0);
UPDATE #Prezzi SET PrezzoNetto = PrezzoNetto * (1 - S3 / 100.0);
UPDATE #Prezzi SET PrezzoNetto = PrezzoNetto * (1 - S4 / 100.0);

SELECT
    a.IdArticolo,
    s.Giacenza,
    uv.DataUltimaVendita,
    p.IdFornitore,
    cl.RagioneSociale1 AS Fornitore,
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
    ON p.IdArticolo = s.IdArticolo
LEFT JOIN dbo.tabClienti cl
    ON cl.IdCliente = p.IdFornitore;

SELECT
    IdFornitore AS SupplierId,
    ISNULL(Fornitore, 'FORNITORE NON IDENTIFICATO') AS Supplier,
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
FROM #AnalisiBase
GROUP BY
    IdFornitore,
    ISNULL(Fornitore, 'FORNITORE NON IDENTIFICATO')
ORDER BY FifoValue DESC;
""";

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = 120;

        await using var reader = await command.ExecuteReaderAsync(ct);

        var result = new List<InventorySupplierSummaryDto>();

        while (await reader.ReadAsync(ct))
        {
            result.Add(new InventorySupplierSummaryDto
            {
                SupplierId = reader["SupplierId"] == DBNull.Value
                    ? null
                    : Convert.ToInt32(reader["SupplierId"]),
                Supplier = Convert.ToString(reader["Supplier"]) ?? "",
                Articles = Convert.ToInt32(reader["Articles"]),
                Quantity = Number(reader, "Quantity"),
                FifoValue = Number(reader, "FifoValue"),
                PurchaseListValue = Number(reader, "PurchaseListValue")
            });
        }

        return result;
    }


    public Task<IReadOnlyList<InventoryDimensionSummaryDto>> GetManufacturersAsync(
        CancellationToken ct) =>
        GetDimensionSummaryAsync(InventoryDimension.Manufacturer, ct);

    public Task<IReadOnlyList<InventoryDimensionSummaryDto>> GetFamiliesAsync(
        CancellationToken ct) =>
        GetDimensionSummaryAsync(InventoryDimension.Family, ct);

    public Task<IReadOnlyList<InventoryDimensionSummaryDto>> GetSubFamiliesAsync(
        CancellationToken ct) =>
        GetDimensionSummaryAsync(InventoryDimension.SubFamily, ct);

    public Task<IReadOnlyList<InventoryDimensionSummaryDto>> GetCategoriesAsync(
        CancellationToken ct) =>
        GetDimensionSummaryAsync(InventoryDimension.Category, ct);

    public Task<IReadOnlyList<InventoryDimensionSummaryDto>> GetSubCategoriesAsync(
        CancellationToken ct) =>
        GetDimensionSummaryAsync(InventoryDimension.SubCategory, ct);

    private async Task<IReadOnlyList<InventoryDimensionSummaryDto>> GetDimensionSummaryAsync(
        InventoryDimension dimension,
        CancellationToken ct)
    {
        var (idColumn, nameColumn, missingName) = dimension switch
        {
            InventoryDimension.Manufacturer =>
                ("IdProduttore", "Produttore", "PRODUTTORE NON IDENTIFICATO"),
            InventoryDimension.Family =>
                ("IdFamiglia", "NomeFamiglia", "FAMIGLIA NON IDENTIFICATA"),
            InventoryDimension.SubFamily =>
                ("IdSottoFamiglia", "NomeSottoFamiglia", "SOTTOFAMIGLIA NON IDENTIFICATA"),
            InventoryDimension.Category =>
                ("IdCategoria", "NomeCategoria", "CATEGORIA NON IDENTIFICATA"),
            InventoryDimension.SubCategory =>
                ("IdSottoCategoria", "NomeSottoCategoria", "SOTTOCATEGORIA NON IDENTIFICATA"),
            _ => throw new ArgumentOutOfRangeException(nameof(dimension))
        };

        var sql = $"""
SET NOCOUNT ON;

IF OBJECT_ID('tempdb..#Stock') IS NOT NULL DROP TABLE #Stock;
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
    IdArticolo,
    SUM(ValorizzazioneTot) AS ValoreFIFO
INTO #FIFO
FROM due_val.TabProdGiacFIFO
WHERE IdMagazzino = 0
  AND QtaTotAnalizzata > 0
GROUP BY IdArticolo;

SELECT
    af.IdArticolo,
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

UPDATE #Prezzi SET PrezzoNetto = PrezzoNetto * (1 - S1 / 100.0);
UPDATE #Prezzi SET PrezzoNetto = PrezzoNetto * (1 - S2 / 100.0);
UPDATE #Prezzi SET PrezzoNetto = PrezzoNetto * (1 - S3 / 100.0);
UPDATE #Prezzi SET PrezzoNetto = PrezzoNetto * (1 - S4 / 100.0);

SELECT
    a.IdArticolo,
    s.Giacenza,
    a.IdProduttore,
    prod.Produttore,
    a.IdFamiglia,
    fam.NomeFamiglia,
    a.IdSottoFamiglia,
    sf.NomeSottoFamiglia,
    a.IdCategoria,
    cat.NomeCategoria,
    a.IdSottoCategoria,
    sc.NomeSottoCategoria,
    a.TipoUmAcq,
    a.TipoUmMag,
    a.CoeffConversione,
    ISNULL(p.PrezzoNetto, 0) AS PrezzoNetto,
    ISNULL(f.ValoreFIFO, 0) AS ValoreFIFO
INTO #AnalisiBase
FROM #Stock s
INNER JOIN dbo.TabArticoli a
    ON a.IdArticolo = s.IdArticolo
LEFT JOIN #FIFO f
    ON f.IdArticolo = s.IdArticolo
LEFT JOIN #Prezzi p
    ON p.IdArticolo = s.IdArticolo
LEFT JOIN dbo.tabProduttori prod
    ON prod.IdProduttore = a.IdProduttore
LEFT JOIN dbo.TabFamiglie fam
    ON fam.IdFamiglia = a.IdFamiglia
LEFT JOIN dbo.TabSottoFamiglie sf
    ON sf.IdFamiglia = a.IdFamiglia
   AND sf.IdSottoFamiglia = a.IdSottoFamiglia
LEFT JOIN dbo.tabCategorie cat
    ON cat.IdCategoria = a.IdCategoria
LEFT JOIN dbo.tabSottoCategorie sc
    ON sc.IdCategoria = a.IdCategoria
   AND sc.IdSottoCategoria = a.IdSottoCategoria
OUTER APPLY
(
    SELECT TOP (1)
        LTRIM(RTRIM(b.BarCode)) AS Barcode
    FROM dbo.tabBarcode b
    WHERE b.idArticolo = a.IdArticolo
      AND NULLIF(LTRIM(RTRIM(b.BarCode)), '') IS NOT NULL
      AND ISNULL(b.Annullato, 0) = 0
      AND ISNULL(b.idVariante1, 0) <= 0
      AND ISNULL(b.idVariante2, 0) <= 0
      AND ISNULL(b.idVariante3, 0) <= 0
    ORDER BY
        CASE
            WHEN LEN(LTRIM(RTRIM(b.BarCode))) = 13 THEN 0
            ELSE 1
        END,
        b.DataCreaz,
        b.BarCode
) barcode;

SELECT
    {idColumn} AS DimensionId,
    ISNULL(NULLIF(LTRIM(RTRIM({nameColumn})), ''), '{missingName}') AS DimensionName,
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
FROM #AnalisiBase
GROUP BY
    {idColumn},
    ISNULL(NULLIF(LTRIM(RTRIM({nameColumn})), ''), '{missingName}')
ORDER BY FifoValue DESC;
""";

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = 120;

        await using var reader = await command.ExecuteReaderAsync(ct);

        var result = new List<InventoryDimensionSummaryDto>();

        while (await reader.ReadAsync(ct))
        {
            result.Add(new InventoryDimensionSummaryDto
            {
                Id = reader["DimensionId"] == DBNull.Value
                    ? null
                    : Convert.ToInt32(reader["DimensionId"]),
                Name = Convert.ToString(reader["DimensionName"]) ?? "",
                Articles = Convert.ToInt32(reader["Articles"]),
                Quantity = Number(reader, "Quantity"),
                FifoValue = Number(reader, "FifoValue"),
                PurchaseListValue = Number(reader, "PurchaseListValue")
            });
        }

        return result;
    }


    public async Task<IReadOnlyList<InventoryDimensionSummaryDto>> GetClassificationSummaryAsync(
        string dimension,
        InventoryAnalysisFilterDto filter,
        CancellationToken ct)
    {
        var normalized = (dimension ?? "").Trim().ToLowerInvariant();

        var (idColumn, nameColumn, missingName) = normalized switch
        {
            "family" =>
                ("IdFamiglia", "NomeFamiglia", "FAMIGLIA NON IDENTIFICATA"),
            "subfamily" =>
                ("IdSottoFamiglia", "NomeSottoFamiglia", "SOTTOFAMIGLIA NON IDENTIFICATA"),
            "category" =>
                ("IdCategoria", "NomeCategoria", "CATEGORIA NON IDENTIFICATA"),
            "subcategory" =>
                ("IdSottoCategoria", "NomeSottoCategoria", "SOTTOCATEGORIA NON IDENTIFICATA"),
            _ => throw new ArgumentException(
                "dimension deve essere family, subfamily, category oppure subcategory.")
        };

        var sql = $"""
SET NOCOUNT ON;

IF OBJECT_ID('tempdb..#Stock') IS NOT NULL DROP TABLE #Stock;
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
    IdArticolo,
    SUM(ValorizzazioneTot) AS ValoreFIFO
INTO #FIFO
FROM due_val.TabProdGiacFIFO
WHERE IdMagazzino = 0
  AND QtaTotAnalizzata > 0
GROUP BY IdArticolo;

SELECT
    af.IdArticolo,
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

UPDATE #Prezzi SET PrezzoNetto = PrezzoNetto * (1 - S1 / 100.0);
UPDATE #Prezzi SET PrezzoNetto = PrezzoNetto * (1 - S2 / 100.0);
UPDATE #Prezzi SET PrezzoNetto = PrezzoNetto * (1 - S3 / 100.0);
UPDATE #Prezzi SET PrezzoNetto = PrezzoNetto * (1 - S4 / 100.0);

SELECT
    a.IdArticolo,
    s.Giacenza,
    a.IdFamiglia,
    fam.NomeFamiglia,
    a.IdSottoFamiglia,
    sf.NomeSottoFamiglia,
    a.IdCategoria,
    cat.NomeCategoria,
    a.IdSottoCategoria,
    sc.NomeSottoCategoria,
    a.TipoUmAcq,
    a.TipoUmMag,
    a.CoeffConversione,
    ISNULL(p.PrezzoNetto, 0) AS PrezzoNetto,
    ISNULL(f.ValoreFIFO, 0) AS ValoreFIFO
INTO #AnalisiBase
FROM #Stock s
INNER JOIN dbo.TabArticoli a
    ON a.IdArticolo = s.IdArticolo
LEFT JOIN #FIFO f
    ON f.IdArticolo = s.IdArticolo
LEFT JOIN #Prezzi p
    ON p.IdArticolo = s.IdArticolo
LEFT JOIN dbo.TabFamiglie fam
    ON fam.IdFamiglia = a.IdFamiglia
LEFT JOIN dbo.TabSottoFamiglie sf
    ON sf.IdFamiglia = a.IdFamiglia
   AND sf.IdSottoFamiglia = a.IdSottoFamiglia
LEFT JOIN dbo.tabCategorie cat
    ON cat.IdCategoria = a.IdCategoria
LEFT JOIN dbo.tabSottoCategorie sc
    ON sc.IdCategoria = a.IdCategoria
   AND sc.IdSottoCategoria = a.IdSottoCategoria;

SELECT
    {idColumn} AS DimensionId,
    ISNULL(NULLIF(LTRIM(RTRIM({nameColumn})), ''), '{missingName}') AS DimensionName,
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
FROM #AnalisiBase
WHERE
    (@FamilyId IS NULL OR IdFamiglia = @FamilyId)
    AND (@SubFamilyId IS NULL OR IdSottoFamiglia = @SubFamilyId)
    AND (@CategoryId IS NULL OR IdCategoria = @CategoryId)
    AND (@SubCategoryId IS NULL OR IdSottoCategoria = @SubCategoryId)
GROUP BY
    {idColumn},
    ISNULL(NULLIF(LTRIM(RTRIM({nameColumn})), ''), '{missingName}')
ORDER BY FifoValue DESC, DimensionName;
""";

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = 120;

        command.Parameters.AddWithValue("@FamilyId", (object?)filter.FamilyId ?? DBNull.Value);
        command.Parameters.AddWithValue("@SubFamilyId", (object?)filter.SubFamilyId ?? DBNull.Value);
        command.Parameters.AddWithValue("@CategoryId", (object?)filter.CategoryId ?? DBNull.Value);
        command.Parameters.AddWithValue("@SubCategoryId", (object?)filter.SubCategoryId ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<InventoryDimensionSummaryDto>();

        while (await reader.ReadAsync(ct))
        {
            result.Add(new InventoryDimensionSummaryDto
            {
                Id = reader["DimensionId"] == DBNull.Value
                    ? null
                    : Convert.ToInt32(reader["DimensionId"]),
                Name = Convert.ToString(reader["DimensionName"]) ?? "",
                Articles = Convert.ToInt32(reader["Articles"]),
                Quantity = Number(reader, "Quantity"),
                FifoValue = Number(reader, "FifoValue"),
                PurchaseListValue = Number(reader, "PurchaseListValue")
            });
        }

        return result;
    }


    private enum InventoryDimension
    {
        Manufacturer,
        Family,
        SubFamily,
        Category,
        SubCategory
    }


    public async Task<IReadOnlyList<InventoryAnalysisItemDto>> GetItemsAsync(
        InventoryAnalysisFilterDto filter,
        CancellationToken ct)
    {
        const string sql = """
SET NOCOUNT ON;

DECLARE @DataAnalisi datetime = GETDATE();

IF OBJECT_ID('tempdb..#Stock') IS NOT NULL DROP TABLE #Stock;
IF OBJECT_ID('tempdb..#Vendite') IS NOT NULL DROP TABLE #Vendite;
IF OBJECT_ID('tempdb..#FIFO') IS NOT NULL DROP TABLE #FIFO;
IF OBJECT_ID('tempdb..#Prezzi') IS NOT NULL DROP TABLE #Prezzi;
IF OBJECT_ID('tempdb..#Analisi') IS NOT NULL DROP TABLE #Analisi;

SELECT g.IdArticolo, g.Giacenza
INTO #Stock
FROM dbo.tabGiacenzeStoreView g
INNER JOIN dbo.TabArticoli a ON a.IdArticolo = g.IdArticolo
WHERE g.idMagazzinoStore = 0
  AND g.Giacenza > 0
  AND a.Attivo = 1;

;WITH VenditeUnificate AS
(
    SELECT
        d.IdArticolo,
        t.DataOraScontrino AS DataVendita,
        ABS(CAST(d.Quantita AS decimal(18,4))) AS Quantita
    FROM dbo.tabTestateScontrini t
    INNER JOIN dbo.tabDettaglioScontrini d
        ON d.idTestata = t.idTestata
    INNER JOIN #Stock s
        ON s.IdArticolo = d.IdArticolo
    WHERE t.IdCausale = 4
      AND d.IdArticolo IS NOT NULL
      AND d.IdArticolo > 0
      AND ISNULL(d.Quantita, 0) <> 0
      AND t.DataOraScontrino <= @DataAnalisi

    UNION ALL

    SELECT
        d.IdArticolo,
        f.DataDocumento AS DataVendita,
        ABS(CAST(d.Quantita AS decimal(18,4))) AS Quantita
    FROM dbo.tabTestateFatture f
    INNER JOIN dbo.tabDettaglioFatture d
        ON d.IdTestata = f.IdTestata
    INNER JOIN #Stock s
        ON s.IdArticolo = d.IdArticolo
    WHERE f.IdCausale = 27
      AND d.IdArticolo IS NOT NULL
      AND d.IdArticolo > 0
      AND ISNULL(d.Quantita, 0) <> 0
      AND f.DataDocumento <= @DataAnalisi
)
SELECT
    IdArticolo,
    MAX(DataVendita) AS DataUltimaVendita,
    SUM(CASE
            WHEN DataVendita >= DATEADD(YEAR, -1, @DataAnalisi)
            THEN Quantita ELSE 0
        END) AS Venduto12M,
    SUM(CASE
            WHEN DataVendita >= DATEADD(YEAR, -2, @DataAnalisi)
             AND DataVendita < DATEADD(YEAR, -1, @DataAnalisi)
            THEN Quantita ELSE 0
        END) AS VendutoAnnoPrecedente,
    COUNT(DISTINCT CASE
            WHEN DataVendita >= DATEADD(YEAR, -1, @DataAnalisi)
            THEN YEAR(DataVendita) * 100 + MONTH(DataVendita)
        END) AS MesiConVendite12M
INTO #Vendite
FROM VenditeUnificate
GROUP BY IdArticolo;

SELECT IdArticolo, SUM(ValorizzazioneTot) AS ValoreFIFO
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

UPDATE #Prezzi SET PrezzoNetto = PrezzoNetto * (1 - S1 / 100.0);
UPDATE #Prezzi SET PrezzoNetto = PrezzoNetto * (1 - S2 / 100.0);
UPDATE #Prezzi SET PrezzoNetto = PrezzoNetto * (1 - S3 / 100.0);
UPDATE #Prezzi SET PrezzoNetto = PrezzoNetto * (1 - S4 / 100.0);

SELECT
    a.IdArticolo,
    a.CodiceArticolo,
    a.Descrizione,
    ISNULL(barcode.Barcode, '') AS Barcode,
    s.Giacenza,
    v.DataUltimaVendita,
    ISNULL(v.Venduto12M, 0) AS Venduto12M,
    ISNULL(v.VendutoAnnoPrecedente, 0) AS VendutoAnnoPrecedente,
    ISNULL(v.MesiConVendite12M, 0) AS MesiConVendite12M,
    CASE
        WHEN v.DataUltimaVendita IS NULL THEN 1
        WHEN v.DataUltimaVendita < DATEADD(YEAR, -5, @DataAnalisi) THEN 2
        WHEN v.DataUltimaVendita < DATEADD(YEAR, -2, @DataAnalisi) THEN 3
        WHEN v.DataUltimaVendita < DATEADD(YEAR, -1, @DataAnalisi) THEN 4
        WHEN v.DataUltimaVendita < DATEADD(MONTH, -6, @DataAnalisi) THEN 5
        ELSE 6
    END AS StatoRotazioneId,
    p.IdFornitore,
    cl.RagioneSociale1 AS Fornitore,
    a.IdProduttore,
    prod.Produttore,
    a.IdFamiglia,
    fam.NomeFamiglia,
    a.IdSottoFamiglia,
    sf.NomeSottoFamiglia,
    a.IdCategoria,
    cat.NomeCategoria,
    a.IdSottoCategoria,
    sc.NomeSottoCategoria,
    ISNULL(f.ValoreFIFO, 0) AS ValoreFIFO,
    s.Giacenza *
    CASE
        WHEN ISNULL(a.TipoUmAcq, 1) = ISNULL(a.TipoUmMag, 1)
            THEN ISNULL(p.PrezzoNetto, 0)
        WHEN ISNULL(a.CoeffConversione, 0) <= 0
            THEN ISNULL(p.PrezzoNetto, 0)
        WHEN a.TipoUmAcq = 1
            THEN ISNULL(p.PrezzoNetto, 0) / a.CoeffConversione
        ELSE
            ISNULL(p.PrezzoNetto, 0) * a.CoeffConversione
    END AS ValoreListinoAcquisto
INTO #Analisi
FROM #Stock s
INNER JOIN dbo.TabArticoli a ON a.IdArticolo = s.IdArticolo
LEFT JOIN #Vendite v ON v.IdArticolo = s.IdArticolo
LEFT JOIN #FIFO f ON f.IdArticolo = s.IdArticolo
LEFT JOIN #Prezzi p ON p.IdArticolo = s.IdArticolo
LEFT JOIN dbo.tabClienti cl ON cl.IdCliente = p.IdFornitore
LEFT JOIN dbo.tabProduttori prod ON prod.IdProduttore = a.IdProduttore
LEFT JOIN dbo.TabFamiglie fam ON fam.IdFamiglia = a.IdFamiglia
LEFT JOIN dbo.TabSottoFamiglie sf
    ON sf.IdFamiglia = a.IdFamiglia
   AND sf.IdSottoFamiglia = a.IdSottoFamiglia
LEFT JOIN dbo.tabCategorie cat ON cat.IdCategoria = a.IdCategoria
LEFT JOIN dbo.tabSottoCategorie sc
    ON sc.IdCategoria = a.IdCategoria
   AND sc.IdSottoCategoria = a.IdSottoCategoria
OUTER APPLY
(
    SELECT TOP (1)
        LTRIM(RTRIM(b.BarCode)) AS Barcode
    FROM dbo.tabBarcode b
    WHERE b.idArticolo = a.IdArticolo
      AND NULLIF(LTRIM(RTRIM(b.BarCode)), '') IS NOT NULL
      AND ISNULL(b.Annullato, 0) = 0
      AND ISNULL(b.idVariante1, 0) <= 0
      AND ISNULL(b.idVariante2, 0) <= 0
      AND ISNULL(b.idVariante3, 0) <= 0
    ORDER BY
        CASE
            WHEN LEN(LTRIM(RTRIM(b.BarCode))) = 13 THEN 0
            ELSE 1
        END,
        b.DataCreaz,
        b.BarCode
) barcode;

SELECT TOP (@Limit)
    IdArticolo, CodiceArticolo, Descrizione, Barcode, Giacenza,
    DataUltimaVendita, StatoRotazioneId,
    IdFornitore, Fornitore,
    IdProduttore, Produttore,
    IdFamiglia, NomeFamiglia,
    IdSottoFamiglia, NomeSottoFamiglia,
    IdCategoria, NomeCategoria,
    IdSottoCategoria, NomeSottoCategoria,
    ValoreFIFO, ValoreListinoAcquisto,
    Venduto12M,
    VendutoAnnoPrecedente,
    MesiConVendite12M,
    CAST(
        CASE
            WHEN ISNULL(Venduto12M, 0) <= 0 THEN NULL
            ELSE Giacenza / (Venduto12M / 12.0)
        END
        AS decimal(18,2)
    ) AS MesiCopertura
FROM #Analisi
WHERE
    (@RotationId IS NULL OR StatoRotazioneId = @RotationId)
    AND (@SupplierId IS NULL OR IdFornitore = @SupplierId)
    AND (@ManufacturerId IS NULL OR IdProduttore = @ManufacturerId)
    AND (@FamilyId IS NULL OR IdFamiglia = @FamilyId)
    AND (@SubFamilyId IS NULL OR IdSottoFamiglia = @SubFamilyId)
    AND (@CategoryId IS NULL OR IdCategoria = @CategoryId)
    AND (@SubCategoryId IS NULL OR IdSottoCategoria = @SubCategoryId)
    AND (
        @Q IS NULL
        OR CodiceArticolo LIKE '%' + @Q + '%'
        OR Descrizione LIKE '%' + @Q + '%'
        OR Fornitore LIKE '%' + @Q + '%'
        OR Produttore LIKE '%' + @Q + '%'
    )
ORDER BY ValoreFIFO DESC, Descrizione, IdArticolo;
""";

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = 120;

        command.Parameters.AddWithValue("@Limit", Math.Clamp(filter.Limit, 1, 50000));
        command.Parameters.AddWithValue("@RotationId", (object?)filter.RotationId ?? DBNull.Value);
        command.Parameters.AddWithValue("@SupplierId", (object?)filter.SupplierId ?? DBNull.Value);
        command.Parameters.AddWithValue("@ManufacturerId", (object?)filter.ManufacturerId ?? DBNull.Value);
        command.Parameters.AddWithValue("@FamilyId", (object?)filter.FamilyId ?? DBNull.Value);
        command.Parameters.AddWithValue("@SubFamilyId", (object?)filter.SubFamilyId ?? DBNull.Value);
        command.Parameters.AddWithValue("@CategoryId", (object?)filter.CategoryId ?? DBNull.Value);
        command.Parameters.AddWithValue("@SubCategoryId", (object?)filter.SubCategoryId ?? DBNull.Value);

        var q = string.IsNullOrWhiteSpace(filter.Q) ? null : filter.Q.Trim();
        command.Parameters.AddWithValue("@Q", (object?)q ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<InventoryAnalysisItemDto>();

        while (await reader.ReadAsync(ct))
        {
            var rotationId = Convert.ToInt32(reader["StatoRotazioneId"]);
            var quantity = Number(reader, "Giacenza");
            var fifoValue = Number(reader, "ValoreFIFO");
            var sold12M = Number(reader, "Venduto12M");
            var soldPreviousYear = Number(reader, "VendutoAnnoPrecedente");
            var monthsWithSales12M = Convert.ToInt32(reader["MesiConVendite12M"]);
            var monthsCoverage = reader["MesiCopertura"] == DBNull.Value
                ? (decimal?)null
                : Convert.ToDecimal(reader["MesiCopertura"]);
            var lastSaleDate = reader["DataUltimaVendita"] == DBNull.Value
                ? (DateTime?)null
                : Convert.ToDateTime(reader["DataUltimaVendita"]);

            var commercialScore = CalculateCommercialScore(
                lastSaleDate,
                sold12M,
                soldPreviousYear,
                monthsWithSales12M);

            var economicScore = CalculateEconomicScore(
                fifoValue,
                sold12M,
                monthsCoverage,
                lastSaleDate);

            result.Add(new InventoryAnalysisItemDto
            {
                ArticleId = Convert.ToInt32(reader["IdArticolo"]),
                ArticleCode = Convert.ToString(reader["CodiceArticolo"]) ?? "",
                Description = Convert.ToString(reader["Descrizione"]) ?? "",
                Barcode = Convert.ToString(reader["Barcode"]) ?? "",
                Quantity = quantity,
                LastSaleDate = lastSaleDate,
                RotationId = rotationId,
                Rotation = RotationName(rotationId),
                SupplierId = reader["IdFornitore"] == DBNull.Value ? null : Convert.ToInt32(reader["IdFornitore"]),
                Supplier = reader["Fornitore"] == DBNull.Value ? null : Convert.ToString(reader["Fornitore"]),
                ManufacturerId = reader["IdProduttore"] == DBNull.Value ? null : Convert.ToInt32(reader["IdProduttore"]),
                Manufacturer = reader["Produttore"] == DBNull.Value ? null : Convert.ToString(reader["Produttore"]),
                FamilyId = reader["IdFamiglia"] == DBNull.Value ? null : Convert.ToInt32(reader["IdFamiglia"]),
                Family = reader["NomeFamiglia"] == DBNull.Value ? null : Convert.ToString(reader["NomeFamiglia"]),
                SubFamilyId = reader["IdSottoFamiglia"] == DBNull.Value ? null : Convert.ToInt32(reader["IdSottoFamiglia"]),
                SubFamily = reader["NomeSottoFamiglia"] == DBNull.Value ? null : Convert.ToString(reader["NomeSottoFamiglia"]),
                CategoryId = reader["IdCategoria"] == DBNull.Value ? null : Convert.ToInt32(reader["IdCategoria"]),
                Category = reader["NomeCategoria"] == DBNull.Value ? null : Convert.ToString(reader["NomeCategoria"]),
                SubCategoryId = reader["IdSottoCategoria"] == DBNull.Value ? null : Convert.ToInt32(reader["IdSottoCategoria"]),
                SubCategory = reader["NomeSottoCategoria"] == DBNull.Value ? null : Convert.ToString(reader["NomeSottoCategoria"]),
                FifoValue = fifoValue,
                PurchaseListValue = Number(reader, "ValoreListinoAcquisto"),
                CommercialScore = commercialScore,
                EconomicScore = economicScore,
                CommercialDescription = BuildCommercialDescription(
                    lastSaleDate,
                    sold12M,
                    soldPreviousYear,
                    monthsWithSales12M),
                EconomicDescription = BuildEconomicDescription(
                    fifoValue,
                    sold12M,
                    monthsCoverage,
                    lastSaleDate)
            });
        }

        return result;
    }


    private static int CalculateCommercialScore(
        DateTime? ultimaVendita,
        decimal venduto12M,
        decimal vendutoAnnoPrecedente,
        int mesiConVendite12M,
        DateTime? referenceDate = null)
    {
        var now = referenceDate ?? DateTime.Now;

        var puntiRecenza =
            ultimaVendita is null ? 45 :
            ultimaVendita < now.AddYears(-10) ? 45 :
            ultimaVendita < now.AddYears(-7) ? 42 :
            ultimaVendita < now.AddYears(-5) ? 38 :
            ultimaVendita < now.AddYears(-3) ? 34 :
            ultimaVendita < now.AddYears(-2) ? 30 :
            ultimaVendita < now.AddYears(-1) ? 25 :
            ultimaVendita < now.AddMonths(-6) ? 18 :
            ultimaVendita < now.AddMonths(-3) ? 10 :
            ultimaVendita < now.AddMonths(-1) ? 5 :
            0;

        var puntiContinuita =
            mesiConVendite12M <= 0 ? 18 :
            mesiConVendite12M == 1 ? 16 :
            mesiConVendite12M == 2 ? 13 :
            mesiConVendite12M == 3 ? 10 :
            mesiConVendite12M <= 5 ? 7 :
            mesiConVendite12M <= 7 ? 4 :
            mesiConVendite12M <= 9 ? 2 :
            0;

        int puntiAndamento;

        if (venduto12M <= 0m && vendutoAnnoPrecedente > 0m)
        {
            puntiAndamento = 15;
        }
        else if (venduto12M <= 0m && vendutoAnnoPrecedente <= 0m)
        {
            puntiAndamento = 12;
        }
        else if (vendutoAnnoPrecedente <= 0m && venduto12M > 0m)
        {
            puntiAndamento = 2;
        }
        else if (venduto12M <= vendutoAnnoPrecedente * 0.50m)
        {
            puntiAndamento = 12;
        }
        else if (venduto12M < vendutoAnnoPrecedente * 0.75m)
        {
            puntiAndamento = 9;
        }
        else if (venduto12M < vendutoAnnoPrecedente)
        {
            puntiAndamento = 6;
        }
        else if (venduto12M >= vendutoAnnoPrecedente * 1.25m)
        {
            puntiAndamento = 0;
        }
        else
        {
            puntiAndamento = 3;
        }

        var puntiIntensita =
            venduto12M <= 0m ? 10 :
            venduto12M <= 1m ? 9 :
            venduto12M <= 3m ? 7 :
            venduto12M <= 6m ? 5 :
            venduto12M <= 12m ? 3 :
            venduto12M <= 24m ? 1 :
            0;

        var score =
            puntiRecenza +
            puntiContinuita +
            puntiAndamento +
            puntiIntensita;

        // Regole forti della simulazione V0.2.
        if (ultimaVendita is null)
        {
            return 100;
        }

        if (ultimaVendita < now.AddYears(-10))
        {
            return 100;
        }

        if (ultimaVendita < now.AddYears(-7))
        {
            score = Math.Max(score, 90);
        }
        else if (ultimaVendita < now.AddYears(-5))
        {
            score = Math.Max(score, 80);
        }

        return Math.Clamp(score, 0, 100);
    }

    private static int CalculateEconomicScore(
        decimal valoreFifo,
        decimal venduto12M,
        decimal? mesiCopertura,
        DateTime? ultimaVendita,
        DateTime? referenceDate = null)
    {
        var now = referenceDate ?? DateTime.Now;

        var puntiValore =
            valoreFifo < 1m ? 0 :
            valoreFifo < 5m ? 5 :
            valoreFifo < 10m ? 10 :
            valoreFifo < 20m ? 15 :
            valoreFifo < 50m ? 22 :
            valoreFifo < 100m ? 30 :
            valoreFifo < 250m ? 37 :
            valoreFifo < 500m ? 42 :
            45;

        int puntiImmobilizzazione;

        if (venduto12M > 0m && mesiCopertura.HasValue)
        {
            var copertura = mesiCopertura.Value;

            puntiImmobilizzazione =
                copertura <= 3m ? 0 :
                copertura <= 6m ? 5 :
                copertura <= 12m ? 10 :
                copertura <= 18m ? 15 :
                copertura <= 24m ? 20 :
                copertura <= 36m ? 28 :
                copertura <= 60m ? 35 :
                42;
        }
        else
        {
            puntiImmobilizzazione =
                ultimaVendita is null ? 55 :
                ultimaVendita < now.AddYears(-10) ? 55 :
                ultimaVendita < now.AddYears(-7) ? 50 :
                ultimaVendita < now.AddYears(-5) ? 45 :
                ultimaVendita < now.AddYears(-3) ? 38 :
                ultimaVendita < now.AddYears(-2) ? 32 :
                ultimaVendita < now.AddYears(-1) ? 25 :
                20;
        }

        return Math.Clamp(
            puntiValore + puntiImmobilizzazione,
            0,
            100);
    }

    private static string BuildCommercialDescription(
        DateTime? ultimaVendita,
        decimal venduto12M,
        decimal vendutoAnnoPrecedente,
        int mesiConVendite12M,
        DateTime? referenceDate = null)
    {
        var now = referenceDate ?? DateTime.Now;

        if (ultimaVendita is null)
        {
            return "Mai venduto nello storico";
        }

        if (ultimaVendita < now.AddYears(-10))
        {
            return "Fermo da oltre 10 anni";
        }

        if (ultimaVendita < now.AddYears(-7))
        {
            return "Fermo da 7-10 anni";
        }

        if (ultimaVendita < now.AddYears(-5))
        {
            return "Fermo da 5-7 anni";
        }

        if (venduto12M <= 0m)
        {
            return "Nessuna vendita negli ultimi 12 mesi";
        }

        if (vendutoAnnoPrecedente > 0m &&
            venduto12M <= vendutoAnnoPrecedente * 0.50m)
        {
            return "Vendite in forte calo rispetto all'anno precedente";
        }

        if (vendutoAnnoPrecedente <= 0m && venduto12M > 0m)
        {
            return "Articolo nuovo o ripartito nelle vendite";
        }

        if (mesiConVendite12M >= 10 &&
            vendutoAnnoPrecedente > 0m &&
            venduto12M >= vendutoAnnoPrecedente * 1.25m)
        {
            return "Vendite continue e in crescita";
        }

        if (mesiConVendite12M >= 7)
        {
            return "Vendite regolari durante l'anno";
        }

        if (mesiConVendite12M >= 4)
        {
            return "Vendite discontinue ma presenti";
        }

        return "Vendite occasionali";
    }

    private static string BuildEconomicDescription(
        decimal valoreFifo,
        decimal venduto12M,
        decimal? mesiCopertura,
        DateTime? ultimaVendita,
        DateTime? referenceDate = null)
    {
        if (valoreFifo < 1m)
        {
            return "Capitale immobilizzato trascurabile";
        }

        if (venduto12M > 0m && mesiCopertura.HasValue)
        {
            var copertura = mesiCopertura.Value;

            if (copertura <= 3m)
            {
                return $"FIFO {valoreFifo:0.00} € - copertura fino a 3 mesi";
            }

            if (copertura <= 6m)
            {
                return $"FIFO {valoreFifo:0.00} € - copertura 3-6 mesi";
            }

            if (copertura <= 12m)
            {
                return $"FIFO {valoreFifo:0.00} € - copertura 6-12 mesi";
            }

            if (copertura <= 24m)
            {
                return $"FIFO {valoreFifo:0.00} € - copertura 12-24 mesi";
            }

            if (copertura <= 60m)
            {
                return $"FIFO {valoreFifo:0.00} € - copertura molto lunga";
            }

            return $"FIFO {valoreFifo:0.00} € - copertura oltre 5 anni";
        }

        if (ultimaVendita is null)
        {
            return $"FIFO {valoreFifo:0.00} € - mai venduto";
        }

        var now = referenceDate ?? DateTime.Now;

        var anniFermo =
            Math.Max(
                0,
                (int)Math.Floor(
                    (now - ultimaVendita.Value).TotalDays / 365.25));

        if (anniFermo >= 1)
        {
            return $"FIFO {valoreFifo:0.00} € - nessuna vendita 12M, fermo da circa {anniFermo} anni";
        }

        return $"FIFO {valoreFifo:0.00} € - nessuna vendita negli ultimi 12 mesi";
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



    public async Task<IReadOnlyList<InventoryAnalysisItemDto>> QueryInventoryAsync(
        string mode,
        int periodMonths,
        int limit,
        CancellationToken ct)
    {
        var normalizedMode = (mode ?? "").Trim().ToLowerInvariant();
        var safePeriodMonths = Math.Clamp(periodMonths, 1, 120);
        var safeLimit = Math.Clamp(limit, 1, 50000);

        if (normalizedMode is not ("never-sold" or "top-sold" or "stopped" or "dead-capital"))
        {
            throw new ArgumentException(
                "mode deve essere never-sold, top-sold, stopped oppure dead-capital.");
        }

        const string sql = """
SET NOCOUNT ON;

DECLARE @DataAnalisi datetime = GETDATE();

IF OBJECT_ID('tempdb..#Stock') IS NOT NULL DROP TABLE #Stock;
IF OBJECT_ID('tempdb..#Vendite') IS NOT NULL DROP TABLE #Vendite;
IF OBJECT_ID('tempdb..#FIFO') IS NOT NULL DROP TABLE #FIFO;
IF OBJECT_ID('tempdb..#Prezzi') IS NOT NULL DROP TABLE #Prezzi;
IF OBJECT_ID('tempdb..#Analisi') IS NOT NULL DROP TABLE #Analisi;

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

;WITH VenditeUnificate AS
(
    SELECT
        d.IdArticolo,
        t.DataOraScontrino AS DataVendita,
        ABS(CAST(d.Quantita AS decimal(18,4))) AS Quantita
    FROM dbo.tabTestateScontrini t
    INNER JOIN dbo.tabDettaglioScontrini d
        ON d.IdTestata = t.IdTestata
    INNER JOIN #Stock s
        ON s.IdArticolo = d.IdArticolo
    WHERE t.IdCausale = 4
      AND d.IdArticolo IS NOT NULL
      AND d.IdArticolo > 0
      AND ISNULL(d.Quantita, 0) <> 0
      AND t.DataOraScontrino <= @DataAnalisi

    UNION ALL

    SELECT
        d.IdArticolo,
        f.DataDocumento AS DataVendita,
        ABS(CAST(d.Quantita AS decimal(18,4))) AS Quantita
    FROM dbo.tabTestateFatture f
    INNER JOIN dbo.tabDettaglioFatture d
        ON d.IdTestata = f.IdTestata
    INNER JOIN #Stock s
        ON s.IdArticolo = d.IdArticolo
    WHERE f.IdCausale = 27
      AND d.IdArticolo IS NOT NULL
      AND d.IdArticolo > 0
      AND ISNULL(d.Quantita, 0) <> 0
      AND f.DataDocumento <= @DataAnalisi
)
SELECT
    IdArticolo,
    MAX(DataVendita) AS DataUltimaVendita,
    SUM(Quantita) AS VendutoStorico,
    SUM(
        CASE
            WHEN DataVendita >= DATEADD(MONTH, -@PeriodMonths, @DataAnalisi)
            THEN Quantita ELSE 0
        END
    ) AS VendutoPeriodo,
    SUM(
        CASE
            WHEN DataVendita >= DATEADD(YEAR, -1, @DataAnalisi)
            THEN Quantita ELSE 0
        END
    ) AS Venduto12M,
    SUM(
        CASE
            WHEN DataVendita >= DATEADD(YEAR, -2, @DataAnalisi)
             AND DataVendita < DATEADD(YEAR, -1, @DataAnalisi)
            THEN Quantita ELSE 0
        END
    ) AS VendutoAnnoPrecedente,
    COUNT(
        DISTINCT CASE
            WHEN DataVendita >= DATEADD(YEAR, -1, @DataAnalisi)
            THEN YEAR(DataVendita) * 100 + MONTH(DataVendita)
        END
    ) AS MesiConVendite12M
INTO #Vendite
FROM VenditeUnificate
GROUP BY IdArticolo;

SELECT
    IdArticolo,
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

UPDATE #Prezzi SET PrezzoNetto = PrezzoNetto * (1 - S1 / 100.0);
UPDATE #Prezzi SET PrezzoNetto = PrezzoNetto * (1 - S2 / 100.0);
UPDATE #Prezzi SET PrezzoNetto = PrezzoNetto * (1 - S3 / 100.0);
UPDATE #Prezzi SET PrezzoNetto = PrezzoNetto * (1 - S4 / 100.0);

SELECT
    a.IdArticolo,
    a.CodiceArticolo,
    a.Descrizione,
    ISNULL(barcode.Barcode, '') AS Barcode,
    s.Giacenza,
    v.DataUltimaVendita,
    ISNULL(v.VendutoStorico, 0) AS VendutoStorico,
    ISNULL(v.VendutoPeriodo, 0) AS VendutoPeriodo,
    ISNULL(v.Venduto12M, 0) AS Venduto12M,
    ISNULL(v.VendutoAnnoPrecedente, 0) AS VendutoAnnoPrecedente,
    ISNULL(v.MesiConVendite12M, 0) AS MesiConVendite12M,
    CASE
        WHEN v.DataUltimaVendita IS NULL THEN 1
        WHEN v.DataUltimaVendita < DATEADD(YEAR, -5, @DataAnalisi) THEN 2
        WHEN v.DataUltimaVendita < DATEADD(YEAR, -2, @DataAnalisi) THEN 3
        WHEN v.DataUltimaVendita < DATEADD(YEAR, -1, @DataAnalisi) THEN 4
        WHEN v.DataUltimaVendita < DATEADD(MONTH, -6, @DataAnalisi) THEN 5
        ELSE 6
    END AS StatoRotazioneId,
    p.IdFornitore,
    cl.RagioneSociale1 AS Fornitore,
    a.IdProduttore,
    prod.Produttore,
    a.IdFamiglia,
    fam.NomeFamiglia,
    a.IdSottoFamiglia,
    sf.NomeSottoFamiglia,
    a.IdCategoria,
    cat.NomeCategoria,
    a.IdSottoCategoria,
    sc.NomeSottoCategoria,
    ISNULL(f.ValoreFIFO, 0) AS ValoreFIFO,
    s.Giacenza *
    CASE
        WHEN ISNULL(a.TipoUmAcq, 1) = ISNULL(a.TipoUmMag, 1)
            THEN ISNULL(p.PrezzoNetto, 0)
        WHEN ISNULL(a.CoeffConversione, 0) <= 0
            THEN ISNULL(p.PrezzoNetto, 0)
        WHEN a.TipoUmAcq = 1
            THEN ISNULL(p.PrezzoNetto, 0) / a.CoeffConversione
        ELSE
            ISNULL(p.PrezzoNetto, 0) * a.CoeffConversione
    END AS ValoreListinoAcquisto
INTO #Analisi
FROM #Stock s
INNER JOIN dbo.TabArticoli a
    ON a.IdArticolo = s.IdArticolo
LEFT JOIN #Vendite v
    ON v.IdArticolo = s.IdArticolo
LEFT JOIN #FIFO f
    ON f.IdArticolo = s.IdArticolo
LEFT JOIN #Prezzi p
    ON p.IdArticolo = s.IdArticolo
LEFT JOIN dbo.tabClienti cl
    ON cl.IdCliente = p.IdFornitore
LEFT JOIN dbo.tabProduttori prod
    ON prod.IdProduttore = a.IdProduttore
LEFT JOIN dbo.TabFamiglie fam
    ON fam.IdFamiglia = a.IdFamiglia
LEFT JOIN dbo.TabSottoFamiglie sf
    ON sf.IdFamiglia = a.IdFamiglia
   AND sf.IdSottoFamiglia = a.IdSottoFamiglia
LEFT JOIN dbo.tabCategorie cat
    ON cat.IdCategoria = a.IdCategoria
LEFT JOIN dbo.tabSottoCategorie sc
    ON sc.IdCategoria = a.IdCategoria
   AND sc.IdSottoCategoria = a.IdSottoCategoria
OUTER APPLY
(
    SELECT TOP (1)
        LTRIM(RTRIM(b.BarCode)) AS Barcode
    FROM dbo.tabBarcode b
    WHERE b.IdArticolo = a.IdArticolo
      AND NULLIF(LTRIM(RTRIM(b.BarCode)), '') IS NOT NULL
      AND ISNULL(b.Annullato, 0) = 0
      AND ISNULL(b.IdVariante1, 0) <= 0
      AND ISNULL(b.IdVariante2, 0) <= 0
      AND ISNULL(b.IdVariante3, 0) <= 0
    ORDER BY
        CASE
            WHEN LEN(LTRIM(RTRIM(b.BarCode))) = 13 THEN 0
            ELSE 1
        END,
        b.DataCreaz,
        b.BarCode
) barcode;

SELECT TOP (@Limit)
    IdArticolo,
    CodiceArticolo,
    Descrizione,
    Barcode,
    Giacenza,
    DataUltimaVendita,
    StatoRotazioneId,
    IdFornitore,
    Fornitore,
    IdProduttore,
    Produttore,
    IdFamiglia,
    NomeFamiglia,
    IdSottoFamiglia,
    NomeSottoFamiglia,
    IdCategoria,
    NomeCategoria,
    IdSottoCategoria,
    NomeSottoCategoria,
    ValoreFIFO,
    ValoreListinoAcquisto,
    VendutoStorico,
    VendutoPeriodo,
    Venduto12M,
    VendutoAnnoPrecedente,
    MesiConVendite12M,
    CAST(
        CASE
            WHEN ISNULL(Venduto12M, 0) <= 0 THEN NULL
            ELSE Giacenza / (Venduto12M / 12.0)
        END
        AS decimal(18,2)
    ) AS MesiCopertura
FROM #Analisi
WHERE
    (
        @Mode = 'never-sold'
        AND ISNULL(VendutoStorico, 0) <= 0
    )
    OR
    (
        @Mode = 'top-sold'
        AND ISNULL(VendutoPeriodo, 0) > 0
    )
    OR
    (
        @Mode = 'stopped'
        AND ISNULL(VendutoStorico, 0) > 0
        AND DataUltimaVendita IS NOT NULL
        AND DataUltimaVendita < DATEADD(MONTH, -@PeriodMonths, @DataAnalisi)
    )
    OR
    (
        @Mode = 'dead-capital'
        AND (
            DataUltimaVendita IS NULL
            OR ISNULL(Venduto12M, 0) <= 0
            OR DataUltimaVendita < DATEADD(MONTH, -6, @DataAnalisi)
        )
    )
ORDER BY
    CASE WHEN @Mode = 'never-sold' THEN ValoreFIFO END DESC,
    CASE WHEN @Mode = 'top-sold' THEN VendutoPeriodo END DESC,
    CASE WHEN @Mode = 'stopped' THEN
        CASE WHEN DataUltimaVendita IS NULL THEN 0 ELSE 1 END
    END ASC,
    CASE WHEN @Mode = 'stopped' THEN DataUltimaVendita END ASC,
    CASE WHEN @Mode = 'dead-capital' THEN ValoreFIFO END DESC,
    ValoreFIFO DESC,
    Descrizione,
    IdArticolo;
""";

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = 180;

        command.Parameters.AddWithValue("@Mode", normalizedMode);
        command.Parameters.AddWithValue("@PeriodMonths", safePeriodMonths);
        command.Parameters.AddWithValue("@Limit", safeLimit);

        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<InventoryAnalysisItemDto>();

        while (await reader.ReadAsync(ct))
        {
            var rotationId = Convert.ToInt32(reader["StatoRotazioneId"]);
            var quantity = Number(reader, "Giacenza");
            var fifoValue = Number(reader, "ValoreFIFO");
            var sold12M = Number(reader, "Venduto12M");
            var soldPreviousYear = Number(reader, "VendutoAnnoPrecedente");
            var monthsWithSales12M = Convert.ToInt32(reader["MesiConVendite12M"]);
            var monthsCoverage = reader["MesiCopertura"] == DBNull.Value
                ? (decimal?)null
                : Convert.ToDecimal(reader["MesiCopertura"]);
            var lastSaleDate = reader["DataUltimaVendita"] == DBNull.Value
                ? (DateTime?)null
                : Convert.ToDateTime(reader["DataUltimaVendita"]);

            var commercialScore = CalculateCommercialScore(
                lastSaleDate,
                sold12M,
                soldPreviousYear,
                monthsWithSales12M);

            var economicScore = CalculateEconomicScore(
                fifoValue,
                sold12M,
                monthsCoverage,
                lastSaleDate);

            result.Add(new InventoryAnalysisItemDto
            {
                ArticleId = Convert.ToInt32(reader["IdArticolo"]),
                ArticleCode = Convert.ToString(reader["CodiceArticolo"]) ?? "",
                Description = Convert.ToString(reader["Descrizione"]) ?? "",
                Barcode = Convert.ToString(reader["Barcode"]) ?? "",
                Quantity = quantity,
                LastSaleDate = lastSaleDate,
                SoldPeriod = Number(reader, "VendutoPeriodo"),
                Sold12M = sold12M,
                SoldHistorical = Number(reader, "VendutoStorico"),
                MonthsCoverage = monthsCoverage,
                RotationId = rotationId,
                Rotation = RotationName(rotationId),
                SupplierId = reader["IdFornitore"] == DBNull.Value
                    ? null
                    : Convert.ToInt32(reader["IdFornitore"]),
                Supplier = reader["Fornitore"] == DBNull.Value
                    ? null
                    : Convert.ToString(reader["Fornitore"]),
                ManufacturerId = reader["IdProduttore"] == DBNull.Value
                    ? null
                    : Convert.ToInt32(reader["IdProduttore"]),
                Manufacturer = reader["Produttore"] == DBNull.Value
                    ? null
                    : Convert.ToString(reader["Produttore"]),
                FamilyId = reader["IdFamiglia"] == DBNull.Value
                    ? null
                    : Convert.ToInt32(reader["IdFamiglia"]),
                Family = reader["NomeFamiglia"] == DBNull.Value
                    ? null
                    : Convert.ToString(reader["NomeFamiglia"]),
                SubFamilyId = reader["IdSottoFamiglia"] == DBNull.Value
                    ? null
                    : Convert.ToInt32(reader["IdSottoFamiglia"]),
                SubFamily = reader["NomeSottoFamiglia"] == DBNull.Value
                    ? null
                    : Convert.ToString(reader["NomeSottoFamiglia"]),
                CategoryId = reader["IdCategoria"] == DBNull.Value
                    ? null
                    : Convert.ToInt32(reader["IdCategoria"]),
                Category = reader["NomeCategoria"] == DBNull.Value
                    ? null
                    : Convert.ToString(reader["NomeCategoria"]),
                SubCategoryId = reader["IdSottoCategoria"] == DBNull.Value
                    ? null
                    : Convert.ToInt32(reader["IdSottoCategoria"]),
                SubCategory = reader["NomeSottoCategoria"] == DBNull.Value
                    ? null
                    : Convert.ToString(reader["NomeSottoCategoria"]),
                FifoValue = fifoValue,
                PurchaseListValue = Number(reader, "ValoreListinoAcquisto"),
                CommercialScore = commercialScore,
                EconomicScore = economicScore,
                CommercialDescription = BuildCommercialDescription(
                    lastSaleDate,
                    sold12M,
                    soldPreviousYear,
                    monthsWithSales12M),
                EconomicDescription = BuildEconomicDescription(
                    fifoValue,
                    sold12M,
                    monthsCoverage,
                    lastSaleDate)
            });
        }

        return result;
    }


    public Task<IReadOnlyList<InventoryAnalysisItemDto>> GetReportItemsAsync(
        InventoryAnalysisReportRequest request,
        CancellationToken ct)
    {
        var stockDate = (request.StockDate ?? DateTime.Today).Date;

        // Se il report nasce da INTERROGA MAGAZZINO, usa la stessa query
        // della lista Android: stessi criteri, stesso periodo, stesso ordine.
        if (!string.IsNullOrWhiteSpace(request.QueryMode))
        {
            return QueryInventoryAsync(
                request.QueryMode,
                Math.Clamp(request.PeriodMonths ?? 12, 1, 120),
                50000,
                ct);
        }

        // OGGI rimane esattamente sul percorso storico già stabile.
        if (stockDate >= DateTime.Today)
        {
            return GetItemsAsync(
                new InventoryAnalysisFilterDto
                {
                    RotationId = request.RotationId,
                    SupplierId = request.SupplierId,
                    ManufacturerId = request.ManufacturerId,
                    FamilyId = request.FamilyId,
                    SubFamilyId = request.SubFamilyId,
                    CategoryId = request.CategoryId,
                    SubCategoryId = request.SubCategoryId,
                    Q = request.Q,
                    Limit = 50000
                },
                ct);
        }

        return GetHistoricalReportItemsAsync(request, stockDate, ct);
    }


    private async Task<IReadOnlyList<InventoryAnalysisItemDto>> GetHistoricalReportItemsAsync(
        InventoryAnalysisReportRequest request,
        DateTime stockDate,
        CancellationToken ct)
    {
        // SQL Server datetime arrotonda ai millisecondi .000/.003/.007:
        // 23:59:59.997 rappresenta in modo sicuro la fine del giorno selezionato.
        var analysisDate = stockDate.Date.AddDays(1).AddMilliseconds(-3);

        const string sql = """
SET NOCOUNT ON;

IF OBJECT_ID('tempdb..#CurrentStock') IS NOT NULL DROP TABLE #CurrentStock;
IF OBJECT_ID('tempdb..#MovimentiDopo') IS NOT NULL DROP TABLE #MovimentiDopo;
IF OBJECT_ID('tempdb..#ScontriniDopo') IS NOT NULL DROP TABLE #ScontriniDopo;
IF OBJECT_ID('tempdb..#Stock') IS NOT NULL DROP TABLE #Stock;
IF OBJECT_ID('tempdb..#Vendite') IS NOT NULL DROP TABLE #Vendite;
IF OBJECT_ID('tempdb..#FIFO') IS NOT NULL DROP TABLE #FIFO;
IF OBJECT_ID('tempdb..#Prezzi') IS NOT NULL DROP TABLE #Prezzi;
IF OBJECT_ID('tempdb..#Analisi') IS NOT NULL DROP TABLE #Analisi;

-- 1. Giacenza corrente reale del magazzino 0.
SELECT
    g.IdArticolo,
    SUM(CAST(g.Giacenza AS decimal(18,4))) AS Giacenza
INTO #CurrentStock
FROM dbo.tabGiacenze g
WHERE g.IdMagazzino = 0
GROUP BY g.IdArticolo;

-- 2. Movimenti successivi alla data, ESCLUSE le vendite cassa causale 4/S.
--    Queste ultime vengono ricostruite direttamente da tabDettaglioScontrini,
--    evitando il doppio conteggio verificato sui casi campione.
SELECT
    d.IdArticolo,
    SUM(
        CASE c.TipoMovimento
            WHEN 'C' THEN  ABS(CAST(d.Quantita AS decimal(18,4)))
            WHEN 'S' THEN -ABS(CAST(d.Quantita AS decimal(18,4)))
            ELSE 0
        END
    ) AS MovimentoDopo
INTO #MovimentiDopo
FROM dbo.tabDettaglioMagazzino d
INNER JOIN dbo.tabTestateMagazzino t
    ON t.IdTestata = d.IdTestata
INNER JOIN dbo.tabCausali c
    ON c.IdCausale = t.IdCausale
WHERE d.IdMagazzino = 0
  AND d.IdArticolo IS NOT NULL
  AND d.IdArticolo > 0
  AND t.DataDocumento > @DataAnalisi
  AND NOT (
      t.IdCausale = 4
      AND c.TipoMovimento = 'S'
  )
GROUP BY d.IdArticolo;

-- 3. Vendite cassa successive alla data.
SELECT
    d.IdArticolo,
    SUM(ABS(CAST(d.Quantita AS decimal(18,4)))) AS VendutoDopo
INTO #ScontriniDopo
FROM dbo.tabDettaglioScontrini d
INNER JOIN dbo.tabTestateScontrini t
    ON t.IdTestata = d.IdTestata
WHERE t.IdCausale = 4
  AND d.IdArticolo IS NOT NULL
  AND d.IdArticolo > 0
  AND ISNULL(d.Quantita, 0) <> 0
  AND t.DataOraScontrino > @DataAnalisi
GROUP BY d.IdArticolo;

-- 4. Ricostruzione giacenza alla data:
--    oggi - movimenti non-cassa dopo la data + vendite cassa dopo la data.
SELECT
    a.IdArticolo,
    CAST(
        ISNULL(cs.Giacenza, 0)
        - ISNULL(md.MovimentoDopo, 0)
        + ISNULL(sd.VendutoDopo, 0)
        AS decimal(18,4)
    ) AS Giacenza
INTO #Stock
FROM dbo.TabArticoli a
LEFT JOIN #CurrentStock cs
    ON cs.IdArticolo = a.IdArticolo
LEFT JOIN #MovimentiDopo md
    ON md.IdArticolo = a.IdArticolo
LEFT JOIN #ScontriniDopo sd
    ON sd.IdArticolo = a.IdArticolo
WHERE a.Attivo = 1
  AND (
        ISNULL(cs.Giacenza, 0)
        - ISNULL(md.MovimentoDopo, 0)
        + ISNULL(sd.VendutoDopo, 0)
      ) > 0;

-- 5. Vendite note FINO alla data selezionata.
;WITH VenditeUnificate AS
(
    SELECT
        d.IdArticolo,
        t.DataOraScontrino AS DataVendita,
        ABS(CAST(d.Quantita AS decimal(18,4))) AS Quantita
    FROM dbo.tabTestateScontrini t
    INNER JOIN dbo.tabDettaglioScontrini d
        ON d.IdTestata = t.IdTestata
    INNER JOIN #Stock s
        ON s.IdArticolo = d.IdArticolo
    WHERE t.IdCausale = 4
      AND d.IdArticolo IS NOT NULL
      AND d.IdArticolo > 0
      AND ISNULL(d.Quantita, 0) <> 0
      AND t.DataOraScontrino <= @DataAnalisi

    UNION ALL

    SELECT
        d.IdArticolo,
        f.DataDocumento AS DataVendita,
        ABS(CAST(d.Quantita AS decimal(18,4))) AS Quantita
    FROM dbo.tabTestateFatture f
    INNER JOIN dbo.tabDettaglioFatture d
        ON d.IdTestata = f.IdTestata
    INNER JOIN #Stock s
        ON s.IdArticolo = d.IdArticolo
    WHERE f.IdCausale = 27
      AND d.IdArticolo IS NOT NULL
      AND d.IdArticolo > 0
      AND ISNULL(d.Quantita, 0) <> 0
      AND f.DataDocumento <= @DataAnalisi
)
SELECT
    IdArticolo,
    MAX(DataVendita) AS DataUltimaVendita,
    SUM(
        CASE
            WHEN DataVendita >= DATEADD(YEAR, -1, @DataAnalisi)
            THEN Quantita ELSE 0
        END
    ) AS Venduto12M,
    SUM(
        CASE
            WHEN DataVendita >= DATEADD(YEAR, -2, @DataAnalisi)
             AND DataVendita < DATEADD(YEAR, -1, @DataAnalisi)
            THEN Quantita ELSE 0
        END
    ) AS VendutoAnnoPrecedente,
    COUNT(
        DISTINCT CASE
            WHEN DataVendita >= DATEADD(YEAR, -1, @DataAnalisi)
            THEN YEAR(DataVendita) * 100 + MONTH(DataVendita)
        END
    ) AS MesiConVendite12M
INTO #Vendite
FROM VenditeUnificate
GROUP BY IdArticolo;

-- 6. FIFO calcolata alla data selezionata tramite la funzione Due.
--    Usiamo un barcode valido dell'articolo, come già avviene nel Gateway.
SELECT
    s.IdArticolo,
    SUM(ISNULL(f.Val, 0)) AS ValoreFIFO
INTO #FIFO
FROM #Stock s
INNER JOIN dbo.TabArticoli a
    ON a.IdArticolo = s.IdArticolo
OUTER APPLY
(
    SELECT TOP (1)
        LTRIM(RTRIM(b.BarCode)) AS Barcode
    FROM dbo.tabBarcode b
    WHERE b.IdArticolo = a.IdArticolo
      AND NULLIF(LTRIM(RTRIM(b.BarCode)), '') IS NOT NULL
      AND ISNULL(b.Annullato, 0) = 0
      AND ISNULL(b.IdVariante1, 0) <= 0
      AND ISNULL(b.IdVariante2, 0) <= 0
      AND ISNULL(b.IdVariante3, 0) <= 0
    ORDER BY
        CASE
            WHEN LEN(LTRIM(RTRIM(b.BarCode))) = 13 THEN 0
            ELSE 1
        END,
        b.DataCreaz,
        b.BarCode
) barcode
OUTER APPLY due_val.GetFiFoByBarCodeAndWarehouse(
    @DataAnalisi,
    barcode.Barcode,
    0
) f
GROUP BY s.IdArticolo;

-- 7. Prezzo acquisto netto attuale del fornitore predefinito.
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

UPDATE #Prezzi SET PrezzoNetto = PrezzoNetto * (1 - S1 / 100.0);
UPDATE #Prezzi SET PrezzoNetto = PrezzoNetto * (1 - S2 / 100.0);
UPDATE #Prezzi SET PrezzoNetto = PrezzoNetto * (1 - S3 / 100.0);
UPDATE #Prezzi SET PrezzoNetto = PrezzoNetto * (1 - S4 / 100.0);

-- 8. Stesso shape del normale GetItemsAsync, ma riferito alla data.
SELECT
    a.IdArticolo,
    a.CodiceArticolo,
    a.Descrizione,
    ISNULL(barcode.Barcode, '') AS Barcode,
    s.Giacenza,
    v.DataUltimaVendita,
    ISNULL(v.Venduto12M, 0) AS Venduto12M,
    ISNULL(v.VendutoAnnoPrecedente, 0) AS VendutoAnnoPrecedente,
    ISNULL(v.MesiConVendite12M, 0) AS MesiConVendite12M,
    CASE
        WHEN v.DataUltimaVendita IS NULL THEN 1
        WHEN v.DataUltimaVendita < DATEADD(YEAR, -5, @DataAnalisi) THEN 2
        WHEN v.DataUltimaVendita < DATEADD(YEAR, -2, @DataAnalisi) THEN 3
        WHEN v.DataUltimaVendita < DATEADD(YEAR, -1, @DataAnalisi) THEN 4
        WHEN v.DataUltimaVendita < DATEADD(MONTH, -6, @DataAnalisi) THEN 5
        ELSE 6
    END AS StatoRotazioneId,
    p.IdFornitore,
    cl.RagioneSociale1 AS Fornitore,
    a.IdProduttore,
    prod.Produttore,
    a.IdFamiglia,
    fam.NomeFamiglia,
    a.IdSottoFamiglia,
    sf.NomeSottoFamiglia,
    a.IdCategoria,
    cat.NomeCategoria,
    a.IdSottoCategoria,
    sc.NomeSottoCategoria,
    ISNULL(f.ValoreFIFO, 0) AS ValoreFIFO,
    s.Giacenza *
    CASE
        WHEN ISNULL(a.TipoUmAcq, 1) = ISNULL(a.TipoUmMag, 1)
            THEN ISNULL(p.PrezzoNetto, 0)
        WHEN ISNULL(a.CoeffConversione, 0) <= 0
            THEN ISNULL(p.PrezzoNetto, 0)
        WHEN a.TipoUmAcq = 1
            THEN ISNULL(p.PrezzoNetto, 0) / a.CoeffConversione
        ELSE
            ISNULL(p.PrezzoNetto, 0) * a.CoeffConversione
    END AS ValoreListinoAcquisto
INTO #Analisi
FROM #Stock s
INNER JOIN dbo.TabArticoli a
    ON a.IdArticolo = s.IdArticolo
LEFT JOIN #Vendite v
    ON v.IdArticolo = s.IdArticolo
LEFT JOIN #FIFO f
    ON f.IdArticolo = s.IdArticolo
LEFT JOIN #Prezzi p
    ON p.IdArticolo = s.IdArticolo
LEFT JOIN dbo.tabClienti cl
    ON cl.IdCliente = p.IdFornitore
LEFT JOIN dbo.tabProduttori prod
    ON prod.IdProduttore = a.IdProduttore
LEFT JOIN dbo.TabFamiglie fam
    ON fam.IdFamiglia = a.IdFamiglia
LEFT JOIN dbo.TabSottoFamiglie sf
    ON sf.IdFamiglia = a.IdFamiglia
   AND sf.IdSottoFamiglia = a.IdSottoFamiglia
LEFT JOIN dbo.tabCategorie cat
    ON cat.IdCategoria = a.IdCategoria
LEFT JOIN dbo.tabSottoCategorie sc
    ON sc.IdCategoria = a.IdCategoria
   AND sc.IdSottoCategoria = a.IdSottoCategoria
OUTER APPLY
(
    SELECT TOP (1)
        LTRIM(RTRIM(b.BarCode)) AS Barcode
    FROM dbo.tabBarcode b
    WHERE b.IdArticolo = a.IdArticolo
      AND NULLIF(LTRIM(RTRIM(b.BarCode)), '') IS NOT NULL
      AND ISNULL(b.Annullato, 0) = 0
      AND ISNULL(b.IdVariante1, 0) <= 0
      AND ISNULL(b.IdVariante2, 0) <= 0
      AND ISNULL(b.IdVariante3, 0) <= 0
    ORDER BY
        CASE
            WHEN LEN(LTRIM(RTRIM(b.BarCode))) = 13 THEN 0
            ELSE 1
        END,
        b.DataCreaz,
        b.BarCode
) barcode;

SELECT TOP (@Limit)
    IdArticolo,
    CodiceArticolo,
    Descrizione,
    Barcode,
    Giacenza,
    DataUltimaVendita,
    StatoRotazioneId,
    IdFornitore,
    Fornitore,
    IdProduttore,
    Produttore,
    IdFamiglia,
    NomeFamiglia,
    IdSottoFamiglia,
    NomeSottoFamiglia,
    IdCategoria,
    NomeCategoria,
    IdSottoCategoria,
    NomeSottoCategoria,
    ValoreFIFO,
    ValoreListinoAcquisto,
    Venduto12M,
    VendutoAnnoPrecedente,
    MesiConVendite12M,
    CAST(
        CASE
            WHEN ISNULL(Venduto12M, 0) <= 0 THEN NULL
            ELSE Giacenza / (Venduto12M / 12.0)
        END
        AS decimal(18,2)
    ) AS MesiCopertura
FROM #Analisi
WHERE
    (@RotationId IS NULL OR StatoRotazioneId = @RotationId)
    AND (@SupplierId IS NULL OR IdFornitore = @SupplierId)
    AND (@ManufacturerId IS NULL OR IdProduttore = @ManufacturerId)
    AND (@FamilyId IS NULL OR IdFamiglia = @FamilyId)
    AND (@SubFamilyId IS NULL OR IdSottoFamiglia = @SubFamilyId)
    AND (@CategoryId IS NULL OR IdCategoria = @CategoryId)
    AND (@SubCategoryId IS NULL OR IdSottoCategoria = @SubCategoryId)
    AND (
        @Q IS NULL
        OR CodiceArticolo LIKE '%' + @Q + '%'
        OR Descrizione LIKE '%' + @Q + '%'
        OR Fornitore LIKE '%' + @Q + '%'
        OR Produttore LIKE '%' + @Q + '%'
    )
ORDER BY ValoreFIFO DESC, Descrizione, IdArticolo;
""";

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = 300;

        command.Parameters.AddWithValue("@DataAnalisi", analysisDate);
        command.Parameters.AddWithValue("@Limit", 50000);
        command.Parameters.AddWithValue("@RotationId", (object?)request.RotationId ?? DBNull.Value);
        command.Parameters.AddWithValue("@SupplierId", (object?)request.SupplierId ?? DBNull.Value);
        command.Parameters.AddWithValue("@ManufacturerId", (object?)request.ManufacturerId ?? DBNull.Value);
        command.Parameters.AddWithValue("@FamilyId", (object?)request.FamilyId ?? DBNull.Value);
        command.Parameters.AddWithValue("@SubFamilyId", (object?)request.SubFamilyId ?? DBNull.Value);
        command.Parameters.AddWithValue("@CategoryId", (object?)request.CategoryId ?? DBNull.Value);
        command.Parameters.AddWithValue("@SubCategoryId", (object?)request.SubCategoryId ?? DBNull.Value);

        var q = string.IsNullOrWhiteSpace(request.Q)
            ? null
            : request.Q.Trim();

        command.Parameters.AddWithValue("@Q", (object?)q ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<InventoryAnalysisItemDto>();

        while (await reader.ReadAsync(ct))
        {
            var rotationId = Convert.ToInt32(reader["StatoRotazioneId"]);
            var quantity = Number(reader, "Giacenza");
            var fifoValue = Number(reader, "ValoreFIFO");
            var sold12M = Number(reader, "Venduto12M");
            var soldPreviousYear = Number(reader, "VendutoAnnoPrecedente");
            var monthsWithSales12M = Convert.ToInt32(reader["MesiConVendite12M"]);
            var monthsCoverage = reader["MesiCopertura"] == DBNull.Value
                ? (decimal?)null
                : Convert.ToDecimal(reader["MesiCopertura"]);
            var lastSaleDate = reader["DataUltimaVendita"] == DBNull.Value
                ? (DateTime?)null
                : Convert.ToDateTime(reader["DataUltimaVendita"]);

            var commercialScore = CalculateCommercialScore(
                lastSaleDate,
                sold12M,
                soldPreviousYear,
                monthsWithSales12M,
                analysisDate);

            var economicScore = CalculateEconomicScore(
                fifoValue,
                sold12M,
                monthsCoverage,
                lastSaleDate,
                analysisDate);

            result.Add(new InventoryAnalysisItemDto
            {
                ArticleId = Convert.ToInt32(reader["IdArticolo"]),
                ArticleCode = Convert.ToString(reader["CodiceArticolo"]) ?? "",
                Description = Convert.ToString(reader["Descrizione"]) ?? "",
                Barcode = Convert.ToString(reader["Barcode"]) ?? "",
                Quantity = quantity,
                LastSaleDate = lastSaleDate,
                RotationId = rotationId,
                Rotation = RotationName(rotationId),
                SupplierId = reader["IdFornitore"] == DBNull.Value
                    ? null
                    : Convert.ToInt32(reader["IdFornitore"]),
                Supplier = reader["Fornitore"] == DBNull.Value
                    ? null
                    : Convert.ToString(reader["Fornitore"]),
                ManufacturerId = reader["IdProduttore"] == DBNull.Value
                    ? null
                    : Convert.ToInt32(reader["IdProduttore"]),
                Manufacturer = reader["Produttore"] == DBNull.Value
                    ? null
                    : Convert.ToString(reader["Produttore"]),
                FamilyId = reader["IdFamiglia"] == DBNull.Value
                    ? null
                    : Convert.ToInt32(reader["IdFamiglia"]),
                Family = reader["NomeFamiglia"] == DBNull.Value
                    ? null
                    : Convert.ToString(reader["NomeFamiglia"]),
                SubFamilyId = reader["IdSottoFamiglia"] == DBNull.Value
                    ? null
                    : Convert.ToInt32(reader["IdSottoFamiglia"]),
                SubFamily = reader["NomeSottoFamiglia"] == DBNull.Value
                    ? null
                    : Convert.ToString(reader["NomeSottoFamiglia"]),
                CategoryId = reader["IdCategoria"] == DBNull.Value
                    ? null
                    : Convert.ToInt32(reader["IdCategoria"]),
                Category = reader["NomeCategoria"] == DBNull.Value
                    ? null
                    : Convert.ToString(reader["NomeCategoria"]),
                SubCategoryId = reader["IdSottoCategoria"] == DBNull.Value
                    ? null
                    : Convert.ToInt32(reader["IdSottoCategoria"]),
                SubCategory = reader["NomeSottoCategoria"] == DBNull.Value
                    ? null
                    : Convert.ToString(reader["NomeSottoCategoria"]),
                FifoValue = fifoValue,
                PurchaseListValue = Number(reader, "ValoreListinoAcquisto"),
                CommercialScore = commercialScore,
                EconomicScore = economicScore,
                CommercialDescription = BuildCommercialDescription(
                    lastSaleDate,
                    sold12M,
                    soldPreviousYear,
                    monthsWithSales12M,
                    analysisDate),
                EconomicDescription = BuildEconomicDescription(
                    fifoValue,
                    sold12M,
                    monthsCoverage,
                    lastSaleDate,
                    analysisDate)
            });
        }

        return result;
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