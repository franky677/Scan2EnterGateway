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

IF OBJECT_ID('tempdb..#Stock') IS NOT NULL DROP TABLE #Stock;
IF OBJECT_ID('tempdb..#UltimaVendita') IS NOT NULL DROP TABLE #UltimaVendita;
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

SELECT d.IdArticolo, MAX(t.DataDocumento) AS DataUltimaVendita
INTO #UltimaVendita
FROM dbo.tabDettaglioMagazzino d
INNER JOIN dbo.tabTestateMagazzino t ON t.IdTestata = d.IdTestata
INNER JOIN dbo.tabCausali c ON c.IdCausale = t.IdCausale
INNER JOIN #Stock s ON s.IdArticolo = d.IdArticolo
WHERE d.IdArticolo > 0
  AND c.FlagVendita = 1
  AND d.TipoMovimento = 'S'
GROUP BY d.IdArticolo;

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
    s.Giacenza,
    uv.DataUltimaVendita,
    CASE
        WHEN uv.DataUltimaVendita IS NULL THEN 1
        WHEN uv.DataUltimaVendita < DATEADD(YEAR, -5, GETDATE()) THEN 2
        WHEN uv.DataUltimaVendita < DATEADD(YEAR, -2, GETDATE()) THEN 3
        WHEN uv.DataUltimaVendita < DATEADD(YEAR, -1, GETDATE()) THEN 4
        WHEN uv.DataUltimaVendita < DATEADD(MONTH, -6, GETDATE()) THEN 5
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
LEFT JOIN #UltimaVendita uv ON uv.IdArticolo = s.IdArticolo
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
   AND sc.IdSottoCategoria = a.IdSottoCategoria;

SELECT TOP (@Limit)
    IdArticolo, CodiceArticolo, Descrizione, Giacenza,
    DataUltimaVendita, StatoRotazioneId,
    IdFornitore, Fornitore,
    IdProduttore, Produttore,
    IdFamiglia, NomeFamiglia,
    IdSottoFamiglia, NomeSottoFamiglia,
    IdCategoria, NomeCategoria,
    IdSottoCategoria, NomeSottoCategoria,
    ValoreFIFO, ValoreListinoAcquisto
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

            result.Add(new InventoryAnalysisItemDto
            {
                ArticleId = Convert.ToInt32(reader["IdArticolo"]),
                ArticleCode = Convert.ToString(reader["CodiceArticolo"]) ?? "",
                Description = Convert.ToString(reader["Descrizione"]) ?? "",
                Quantity = Number(reader, "Giacenza"),
                LastSaleDate = reader["DataUltimaVendita"] == DBNull.Value
                    ? null
                    : Convert.ToDateTime(reader["DataUltimaVendita"]),
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
                FifoValue = Number(reader, "ValoreFIFO"),
                PurchaseListValue = Number(reader, "ValoreListinoAcquisto")
            });
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


    public Task<IReadOnlyList<InventoryAnalysisItemDto>> GetReportItemsAsync(
        InventoryAnalysisReportRequest request,
        CancellationToken ct)
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