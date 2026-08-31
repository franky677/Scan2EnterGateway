using System.Globalization;
using Microsoft.Data.SqlClient;
using Scan2EnterGateway.Models;

namespace Scan2EnterGateway.Data;

public sealed class ProductRepository
{
    private readonly string _connectionString;
    private readonly int _warehouseId;

    public ProductRepository(IConfiguration configuration)
    {
        _connectionString =
            configuration.GetConnectionString("DueDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'DueDatabase' is missing.");

        _warehouseId = configuration.GetValue<int?>("Gateway:WarehouseId") ?? 0;
    }

    public async Task<ProductInfoDto?> GetByBarcodeAsync(
        string barcode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(barcode))
        {
            return null;
        }

        const string sql = """
            SELECT TOP (1)
                a.idArticolo,
                a.CodiceArticolo,
                a.Descrizione,
                b.Barcode,
                a.Attivo,
                a.AliquotaIva,
                a.Stagione_Anno,
                a.Stagione_Periodicita,
                price.Imponibile,
                price.PrezzoVendita,
                purchase.Imponibile AS PurchaseTaxable,
                g.Giacenza,
                g.Disponibile,
                s.ScortaMinima,
                s.ScortaMassima,
                s.LottoRiordino,
                af.IdFornitore,
                af.CodiceArticoloFornitore,
                f.Fornitore AS SupplierName,
                u.Ubicazione AS Location
            FROM dbo.tabBarcode AS b
            INNER JOIN dbo.tabArticoli AS a
                ON a.idArticolo = b.idArticolo
            LEFT JOIN dbo.tabGiacenze AS g
                ON g.idArticolo = a.idArticolo
               AND g.idMagazzino = @warehouseId
            LEFT JOIN dbo.TabScortaArticoliView AS s
                ON s.idArticolo = a.idArticolo
               AND s.idMagazzino = @warehouseId
            LEFT JOIN dbo.TabArticoliFornitori AS af
                ON af.IdArticolo = a.idArticolo
               AND af.Predefinito = 1
            LEFT JOIN dbo.ListaFornitori AS f
                ON f.ID = af.IdFornitore
            LEFT JOIN dbo.tabUbicazioniArticoli AS ua
                ON ua.IdArticolo = a.idArticolo
               AND ua.IdUbicazione >= 0
            LEFT JOIN dbo.tabUbicazioni AS u
                ON u.IdUbicazione = ua.IdUbicazione
            OUTER APPLY
            (
                SELECT TOP (1)
                    pv.Imponibile,
                    pv.PrezzoVendita
                FROM dbo.tabPrezziVendita AS pv
                INNER JOIN dbo.TabTipoListini AS tl
                    ON tl.IdListino = pv.IdListino
                WHERE pv.IdArticolo = a.idArticolo
                AND ISNULL(pv.idVariante1, -1) = -1
                AND ISNULL(pv.idVariante2, -1) = -1
                AND ISNULL(pv.idVariante3, -1) = -1
                  AND tl.NomeListino = N'3-AL PUBBLICO'
                ORDER BY
                    tl.predefinito DESC,
                    pv.DataAgg DESC,
                    pv.OraAgg DESC
            ) AS price
            OUTER APPLY
            (
                SELECT TOP (1)
                    p.Imponibile
                FROM dbo.TabPrezziAcquisto AS p
                WHERE p.idFornitore = af.IdFornitore
                  AND LTRIM(RTRIM(p.CodiceArticoloFornitore)) =
                      LTRIM(RTRIM(af.CodiceArticoloFornitore))
                  AND ISNULL(p.idVariante1, -1) = -1
                  AND ISNULL(p.idVariante2, -1) = -1
                  AND ISNULL(p.idVariante3, -1) = -1
                ORDER BY p.dataAgg DESC
            ) AS purchase
            WHERE LTRIM(RTRIM(b.Barcode)) = @barcode
            ORDER BY
                ua.DataAgg DESC,
                a.idArticolo;
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@barcode", barcode.Trim());
        command.Parameters.AddWithValue("@warehouseId", _warehouseId);
        command.CommandTimeout = 30;

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ProductInfoDto
        {
            ArticleId = GetInt64(reader, "idArticolo"),
            ArticleCode = GetString(reader, "CodiceArticolo"),
            Description = GetString(reader, "Descrizione"),
            Barcode = GetString(reader, "Barcode"),
            Active = !reader.IsDBNull(reader.GetOrdinal("Attivo")) &&
                     reader.GetBoolean(reader.GetOrdinal("Attivo")),

            TaxablePrice = GetNumberAsString(reader, "Imponibile"),
            VatRate = GetNumberAsString(reader, "AliquotaIva"),
            PublicPrice = GetNumberAsString(reader, "PrezzoVendita"),
            PurchaseTaxable = GetNumberAsString(reader, "PurchaseTaxable"),

            Season = GetString(reader, "Stagione_Periodicita"),
            Year = GetString(reader, "Stagione_Anno"),
            Location = GetString(reader, "Location"),

            Stock = GetNumberAsString(reader, "Giacenza"),
            AvailableStock = GetNumberAsString(reader, "Disponibile"),
            MinimumStock = GetNullableStockValue(reader, "ScortaMinima"),
            MaximumStock = GetNullableStockValue(reader, "ScortaMassima"),
            ReorderLot = GetNullableStockValue(reader, "LottoRiordino"),

            SupplierId = GetInt64(reader, "IdFornitore"),
            SupplierName = GetString(reader, "SupplierName"),
            SupplierArticleCode =
                GetString(reader, "CodiceArticoloFornitore"),

            CoverImagePath = ""
        };
    }


    public async Task<List<PriceListDto>> GetPriceListsAsync(
        long articleId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT tl.IdListino, tl.NomeListino,
                   pv.Imponibile AS ImponibileVendita,
                   pv.PrezzoVendita,
                   pa.Imponibile AS ImponibileAcquisto
            FROM dbo.tabPrezziVendita AS pv
            INNER JOIN dbo.TabTipoListini AS tl ON tl.IdListino = pv.IdListino
            OUTER APPLY (
                SELECT TOP (1) p.Imponibile
                FROM dbo.TabArticoliFornitori AS af
                INNER JOIN dbo.TabPrezziAcquisto AS p
                    ON p.idFornitore = af.IdFornitore
                   AND LTRIM(RTRIM(p.CodiceArticoloFornitore)) = LTRIM(RTRIM(af.CodiceArticoloFornitore))
                   AND ISNULL(p.idVariante1, -1) = -1
                   AND ISNULL(p.idVariante2, -1) = -1
                   AND ISNULL(p.idVariante3, -1) = -1
                WHERE af.IdArticolo = pv.IdArticolo
                  AND af.Predefinito = 1
                ORDER BY p.dataAgg DESC
            ) AS pa
            WHERE pv.IdArticolo = @articleId
              AND ISNULL(pv.idVariante1, -1) = -1
              AND ISNULL(pv.idVariante2, -1) = -1
              AND ISNULL(pv.idVariante3, -1) = -1
              AND tl.IdListino IN (1, 2, 3, 4, 6)
            ORDER BY tl.IdListino;
            """;

        var results = new List<PriceListDto>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@articleId", articleId);
        command.CommandTimeout = 30;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var saleTaxable = GetNullableDecimal(reader, "ImponibileVendita");
            var purchaseTaxable = GetNullableDecimal(reader, "ImponibileAcquisto");
            decimal? markup = null;
            if (saleTaxable.HasValue && purchaseTaxable.HasValue && purchaseTaxable.Value > 0m)
                markup = ((saleTaxable.Value / purchaseTaxable.Value) - 1m) * 100m;

            results.Add(new PriceListDto
            {
                PriceListId = Convert.ToInt32(reader.GetValue(reader.GetOrdinal("IdListino")), CultureInfo.InvariantCulture),
                Name = GetString(reader, "NomeListino"),
                SaleTaxable = saleTaxable,
                SalePrice = GetNullableDecimal(reader, "PrezzoVendita"),
                PurchaseTaxable = purchaseTaxable,
                EffectiveMarkupPercent = markup.HasValue ? Math.Round(markup.Value, 2) : null
            });
        }
        return results;
    }


    public async Task<bool> UpdatePriceListPriceAsync(
        long articleId,
        int priceListId,
        decimal salePrice,
        CancellationToken cancellationToken = default)
    {
        if (articleId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(articleId),
                "Id articolo non valido.");
        }

        if (priceListId is not (1 or 2 or 3 or 4 or 6))
        {
            throw new ArgumentOutOfRangeException(
                nameof(priceListId),
                "Listino vendita non valido.");
        }

        if (salePrice < 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(salePrice),
                "Il prezzo non può essere negativo.");
        }

        const string sql = """
            UPDATE pv
            SET
                pv.PrezzoVendita = @salePrice,
                pv.Imponibile =
                    ROUND(
                        @salePrice /
                        (
                            1.0 +
                            (
                                ISNULL(a.AliquotaIva, 0.0) / 100.0
                            )
                        ),
                        4
                    ),
                pv.PrezzoImposto = 0,
                pv.PrezzoBloccato = NULL,
                pv.Locked = NULL,
                pv.DataAgg = GETDATE(),
                pv.UtenteUltimoAccesso = N'Franco'
            FROM dbo.tabPrezziVendita AS pv
            INNER JOIN dbo.tabArticoli AS a
                ON a.idArticolo = pv.IdArticolo
            WHERE pv.IdArticolo = @articleId
              AND pv.IdListino = @priceListId
              AND ISNULL(pv.idVariante1, -1) = -1
              AND ISNULL(pv.idVariante2, -1) = -1
              AND ISNULL(pv.idVariante3, -1) = -1;
            """;

        await using var connection =
            new SqlConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);

        await using var command =
            new SqlCommand(sql, connection);

        command.Parameters.AddWithValue(
            "@articleId",
            articleId);

        command.Parameters.AddWithValue(
            "@priceListId",
            priceListId);

        var priceParameter =
            command.Parameters.Add(
                "@salePrice",
                System.Data.SqlDbType.Decimal);

        priceParameter.Precision = 18;
        priceParameter.Scale = 2;
        priceParameter.Value = salePrice;

        command.CommandTimeout = 30;

        var affectedRows =
            await command.ExecuteNonQueryAsync(cancellationToken);

        return affectedRows > 0;
    }


    public async Task<bool> UpdateActiveAsync(
        long articleId,
        bool active,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE dbo.tabArticoli
            SET Attivo = @active
            WHERE idArticolo = @articleId;
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@articleId", articleId);
        command.Parameters.AddWithValue("@active", active);
        command.CommandTimeout = 30;

        var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);
        return affectedRows > 0;
    }


    public async Task<List<SearchResultDto>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var terms = query
            .Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Take(8)
            .ToArray();

        if (terms.Length == 0)
        {
            return [];
        }

        var whereParts = new List<string>();

        for (var index = 0; index < terms.Length; index++)
        {
            whereParts.Add($"""
                (
                    a.CodiceArticolo LIKE '%' + @term{index} + '%'
                    OR a.Descrizione LIKE '%' + @term{index} + '%'
                    OR EXISTS
                    (
                        SELECT 1
                        FROM dbo.tabBarcode AS bSearch{index}
                        WHERE bSearch{index}.idArticolo = a.idArticolo
                          AND bSearch{index}.Barcode LIKE '%' + @term{index} + '%'
                    )
                )
                """);
        }

        var sql = $"""
            SELECT TOP (50)
                a.idArticolo,
                a.CodiceArticolo,
                a.Descrizione,
                barcode.Barcode,
                a.Attivo,
                a.Movimentato,
                a.dataUltimoMovimento,
                g.Giacenza,
                price.PrezzoVendita
            FROM dbo.tabArticoli AS a
            LEFT JOIN dbo.tabGiacenze AS g
                ON g.idArticolo = a.idArticolo
               AND g.idMagazzino = @warehouseId
            OUTER APPLY
            (
                SELECT TOP (1)
                    LTRIM(RTRIM(b1.Barcode)) AS Barcode
                FROM dbo.tabBarcode AS b1
                WHERE b1.idArticolo = a.idArticolo
                  AND NULLIF(LTRIM(RTRIM(b1.Barcode)), '') IS NOT NULL
                ORDER BY
                    CASE
                        WHEN LEN(LTRIM(RTRIM(b1.Barcode))) = 13 THEN 0
                        ELSE 1
                    END,
                    b1.Barcode
            ) AS barcode
            OUTER APPLY
            (
                SELECT TOP (1)
                    pv.PrezzoVendita
                FROM dbo.tabPrezziVendita AS pv
                INNER JOIN dbo.TabTipoListini AS tl
                    ON tl.IdListino = pv.IdListino
                WHERE pv.IdArticolo = a.idArticolo
                  AND ISNULL(pv.idVariante1, -1) = -1
                  AND ISNULL(pv.idVariante2, -1) = -1
                  AND ISNULL(pv.idVariante3, -1) = -1
                  AND tl.NomeListino = N'3-AL PUBBLICO'
                ORDER BY
                    tl.predefinito DESC,
                    pv.DataAgg DESC,
                    pv.OraAgg DESC
            ) AS price
            WHERE
                {string.Join(
                    "\n                AND ",
                    whereParts)}
            ORDER BY
                a.Movimentato DESC,
                a.dataUltimoMovimento DESC,
                a.Descrizione;
            """;

        var results = new List<SearchResultDto>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection);

        for (var index = 0; index < terms.Length; index++)
        {
            command.Parameters.AddWithValue(
                $"@term{index}",
                terms[index]);
        }

        command.Parameters.AddWithValue(
            "@warehouseId",
            _warehouseId);

        command.CommandTimeout = 30;

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        var activeOrdinal =
            reader.GetOrdinal("Attivo");

        var movedOrdinal =
            reader.GetOrdinal("Movimentato");

        var lastMovementOrdinal =
            reader.GetOrdinal("dataUltimoMovimento");

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new SearchResultDto
            {
                Id = GetInt64(reader, "idArticolo"),
                Code = GetString(reader, "CodiceArticolo"),
                Description = GetString(reader, "Descrizione"),
                Barcode = GetString(reader, "Barcode"),
                Price = GetNumberAsString(reader, "PrezzoVendita"),
                Stock = GetNumberAsString(reader, "Giacenza"),
                Active =
                    !reader.IsDBNull(activeOrdinal) &&
                    reader.GetBoolean(activeOrdinal),
                Moved =
                    !reader.IsDBNull(movedOrdinal) &&
                    reader.GetBoolean(movedOrdinal),
                LastMovement =
                    reader.IsDBNull(lastMovementOrdinal)
                        ? null
                        : reader.GetDateTime(
                            lastMovementOrdinal)
            });
        }

        return results;
    }


    public async Task<ProductHealthDto?> GetHealthByBarcodeAsync(
        string barcode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(barcode))
        {
            return null;
        }

        const string sql = """
            DECLARE @DataAnalisi datetime = GETDATE();

            WITH Stock AS
            (
                SELECT
                    g.IdArticolo,
                    g.Barcode,
                    SUM(f.Qty) AS GiacenzaFIFO,
                    SUM(f.Val) AS ValoreFIFO
                FROM dbo.tabGiacenzeStoreView AS g

                CROSS APPLY due_val.GetFiFoByBarCodeAndWarehouse(
                    @DataAnalisi,
                    g.Barcode,
                    g.idMagazzinoStore
                ) AS f

                WHERE g.idMagazzinoStore = @warehouseId
                  AND LTRIM(RTRIM(g.Barcode)) = @barcode

                GROUP BY
                    g.IdArticolo,
                    g.Barcode
            ),

            VenditeDocumenti AS
            (
                -- Scontrini: fonte commerciale immediata.
                SELECT
                    d.IdArticolo,
                    LTRIM(RTRIM(d.Barcode)) AS Barcode,
                    CAST(t.DataOraScontrino AS datetime) AS DataVendita,
                    ABS(CAST(d.Quantita AS decimal(18, 4))) AS Quantita
                FROM dbo.tabDettaglioScontrini AS d
                INNER JOIN dbo.tabTestateScontrini AS t
                    ON t.IdTestata = d.IdTestata
                WHERE LTRIM(RTRIM(d.Barcode)) = @barcode
                  AND t.IdCausale = 4
                  AND d.IdArticolo IS NOT NULL
                  AND d.IdArticolo > 0
                  AND ISNULL(d.Quantita, 0) <> 0
                  AND t.DataOraScontrino <= @DataAnalisi

                UNION ALL

                -- Fatture: fonte commerciale originale.
                -- Non leggiamo anche i movimenti di magazzino causale 25,
                -- perche' rappresentano le stesse vendite e causerebbero doppioni.
                SELECT
                    d.IdArticolo,
                    LTRIM(RTRIM(d.Barcode)) AS Barcode,
                    CAST(t.DataDocumento AS datetime) AS DataVendita,
                    ABS(CAST(d.Quantita AS decimal(18, 4))) AS Quantita
                FROM dbo.tabDettaglioFatture AS d
                INNER JOIN dbo.tabTestateFatture AS t
                    ON t.IdTestata = d.IdTestata
                WHERE LTRIM(RTRIM(d.Barcode)) = @barcode
                  AND t.IdCausale = 27
                  AND d.IdArticolo IS NOT NULL
                  AND d.IdArticolo > 0
                  AND ISNULL(d.Quantita, 0) <> 0
                  AND t.DataDocumento <= @DataAnalisi
            ),

            Vendite AS
            (
                SELECT
                    IdArticolo,
                    Barcode,
                    MAX(DataVendita) AS UltimaVendita,

                    SUM(
                        CASE
                            WHEN DataVendita >= DATEADD(YEAR, -1, @DataAnalisi)
                             AND DataVendita <= @DataAnalisi
                            THEN Quantita
                            ELSE 0
                        END
                    ) AS Venduto12M,

                    SUM(
                        CASE
                            WHEN DataVendita >= DATEADD(YEAR, -2, @DataAnalisi)
                             AND DataVendita <= @DataAnalisi
                            THEN Quantita
                            ELSE 0
                        END
                    ) AS Venduto24M,

                    SUM(
                        CASE
                            WHEN DataVendita >= DATEADD(YEAR, -2, @DataAnalisi)
                             AND DataVendita < DATEADD(YEAR, -1, @DataAnalisi)
                            THEN Quantita
                            ELSE 0
                        END
                    ) AS VendutoAnnoPrecedente,

                    COUNT(
                        DISTINCT CASE
                            WHEN DataVendita >= DATEADD(YEAR, -1, @DataAnalisi)
                             AND DataVendita <= @DataAnalisi
                            THEN YEAR(DataVendita) * 100
                               + MONTH(DataVendita)
                        END
                    ) AS MesiConVendite12M

                FROM VenditeDocumenti

                GROUP BY
                    IdArticolo,
                    Barcode
            ),

            Dati AS
            (
                SELECT
                    s.IdArticolo,
                    s.Barcode,
                    s.GiacenzaFIFO,
                    s.ValoreFIFO,
                    v.UltimaVendita,
                    ISNULL(v.Venduto12M, 0) AS Venduto12M,
                    ISNULL(v.Venduto24M, 0) AS Venduto24M,
                    ISNULL(v.VendutoAnnoPrecedente, 0) AS VendutoAnnoPrecedente,
                    ISNULL(v.MesiConVendite12M, 0) AS MesiConVendite12M
                FROM Stock AS s

                LEFT JOIN Vendite AS v
                    ON v.IdArticolo = s.IdArticolo
                   AND v.Barcode = s.Barcode
            )

            SELECT
                IdArticolo,
                Barcode,

                ROUND(GiacenzaFIFO, 0) AS GiacenzaFIFO,
                ROUND(ValoreFIFO, 2) AS ValoreFIFO,

                CASE
                    WHEN GiacenzaFIFO > 0
                    THEN ROUND(ValoreFIFO / GiacenzaFIFO, 4)
                    ELSE 0
                END AS CostoMedioFIFO,

                UltimaVendita,

                CASE
                    WHEN UltimaVendita IS NULL
                    THEN NULL
                    ELSE DATEDIFF(DAY, UltimaVendita, @DataAnalisi)
                END AS GiorniDaUltimaVendita,

                Venduto12M,
                Venduto24M,
                VendutoAnnoPrecedente,
                MesiConVendite12M,

                CASE
                    WHEN GiacenzaFIFO > 0
                    THEN ROUND(Venduto12M / GiacenzaFIFO, 2)
                    ELSE 0
                END AS Rotazione12M,

                CASE
                    WHEN Venduto12M > 0
                    THEN ROUND(
                        GiacenzaFIFO / (Venduto12M / 12.0),
                        1
                    )
                    ELSE NULL
                END AS MesiCopertura,

                CASE
                    WHEN
                        (
                            UltimaVendita IS NULL
                            OR UltimaVendita < DATEADD(YEAR, -7, @DataAnalisi)
                        )
                        AND ValoreFIFO >= 50
                        THEN 'ROSSO'

                    WHEN
                        (
                            UltimaVendita IS NULL
                            OR UltimaVendita < DATEADD(YEAR, -7, @DataAnalisi)
                        )
                        AND ValoreFIFO >= 20
                        AND ValoreFIFO < 50
                        THEN 'ARANCIONE'

                    WHEN
                        UltimaVendita >= DATEADD(YEAR, -7, @DataAnalisi)
                        AND UltimaVendita < DATEADD(YEAR, -3, @DataAnalisi)
                        AND ValoreFIFO >= 50
                        THEN 'GIALLO'

                    ELSE 'OK'
                END AS StatoSalute,

                CASE
                    WHEN UltimaVendita IS NULL
                        THEN 'Mai venduto nello storico'

                    WHEN UltimaVendita < DATEADD(YEAR, -7, @DataAnalisi)
                        THEN 'Fermo da oltre 7 anni'

                    WHEN UltimaVendita < DATEADD(YEAR, -3, @DataAnalisi)
                        THEN 'Movimentazione molto lenta'

                    WHEN Venduto12M = 0
                        THEN 'Nessuna vendita negli ultimi 12 mesi'

                    WHEN GiacenzaFIFO > 0
                     AND Venduto12M > 0
                     AND GiacenzaFIFO / (Venduto12M / 12.0) > 24
                        THEN 'Copertura oltre 24 mesi'

                    WHEN GiacenzaFIFO > 0
                     AND Venduto12M > 0
                     AND GiacenzaFIFO / (Venduto12M / 12.0) > 12
                        THEN 'Copertura oltre 12 mesi'

                    ELSE 'Regolare'
                END AS DescrizioneSalute

            FROM Dati;
            """;

        await using var connection =
            new SqlConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);

        await using var command =
            new SqlCommand(sql, connection);

        command.Parameters.AddWithValue(
            "@barcode",
            barcode.Trim());

        command.Parameters.AddWithValue(
            "@warehouseId",
            _warehouseId);

        command.CommandTimeout = 60;

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var ultimaVenditaOrdinal =
            reader.GetOrdinal("UltimaVendita");

        var giorniOrdinal =
            reader.GetOrdinal("GiorniDaUltimaVendita");

        var mesiCoperturaOrdinal =
            reader.GetOrdinal("MesiCopertura");

        var ultimaVendita =
            reader.IsDBNull(ultimaVenditaOrdinal)
                ? (DateTime?)null
                : reader.GetDateTime(ultimaVenditaOrdinal);

        var giorniDaUltimaVendita =
            reader.IsDBNull(giorniOrdinal)
                ? (int?)null
                : Convert.ToInt32(
                    reader.GetValue(giorniOrdinal),
                    CultureInfo.InvariantCulture);

        var venduto12M =
            GetNullableDecimal(reader, "Venduto12M") ?? 0m;

        var venduto24M =
            GetNullableDecimal(reader, "Venduto24M") ?? 0m;

        var vendutoAnnoPrecedente =
            GetNullableDecimal(reader, "VendutoAnnoPrecedente") ?? 0m;

        var mesiConVendite12M =
            Math.Min(
                12,
                Convert.ToInt32(
                    reader.GetValue(
                        reader.GetOrdinal("MesiConVendite12M")),
                    CultureInfo.InvariantCulture));

        var giacenzaFifo =
            GetNullableDecimal(reader, "GiacenzaFIFO") ?? 0m;

        var valoreFifo =
            GetNullableDecimal(reader, "ValoreFIFO") ?? 0m;

        var costoMedioFifo =
            GetNullableDecimal(reader, "CostoMedioFIFO") ?? 0m;

        var rotazione12M =
            GetNullableDecimal(reader, "Rotazione12M") ?? 0m;

        var mesiCopertura =
            reader.IsDBNull(mesiCoperturaOrdinal)
                ? (decimal?)null
                : Convert.ToDecimal(
                    reader.GetValue(mesiCoperturaOrdinal),
                    CultureInfo.InvariantCulture);

        var punteggioCommerciale =
            CalculateCommercialScore(
                ultimaVendita,
                venduto12M,
                vendutoAnnoPrecedente,
                mesiConVendite12M);

        var punteggioEconomico =
            CalculateEconomicScore(
                valoreFifo,
                venduto12M,
                mesiCopertura,
                ultimaVendita);

        return new ProductHealthDto
        {
            IdArticolo = GetInt64(reader, "IdArticolo"),
            Barcode = GetString(reader, "Barcode"),

            GiacenzaFifo = giacenzaFifo,
            ValoreFifo = valoreFifo,
            CostoMedioFifo = costoMedioFifo,

            UltimaVendita = ultimaVendita,
            GiorniDaUltimaVendita = giorniDaUltimaVendita,

            Venduto12M = venduto12M,
            Venduto24M = venduto24M,
            VendutoAnnoPrecedente = vendutoAnnoPrecedente,
            MesiConVendite12M = mesiConVendite12M,

            Rotazione12M = rotazione12M,
            MesiCopertura = mesiCopertura,

            // V1: mantenuta invariata per compatibilita' con Android attuale.
            StatoSalute =
                GetString(reader, "StatoSalute"),

            DescrizioneSalute =
                GetString(reader, "DescrizioneSalute"),

            // V2: indici continui 0-100.
            PunteggioCommerciale = punteggioCommerciale,
            PunteggioEconomico = punteggioEconomico,

            DescrizioneCommerciale =
                BuildCommercialDescription(
                    ultimaVendita,
                    venduto12M,
                    vendutoAnnoPrecedente,
                    mesiConVendite12M),

            DescrizioneEconomica =
                BuildEconomicDescription(
                    valoreFifo,
                    venduto12M,
                    mesiCopertura,
                    ultimaVendita)
        };
    }

    private static int CalculateCommercialScore(
        DateTime? ultimaVendita,
        decimal venduto12M,
        decimal vendutoAnnoPrecedente,
        int mesiConVendite12M)
    {
        var now = DateTime.Now;

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
        DateTime? ultimaVendita)
    {
        var now = DateTime.Now;

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
        int mesiConVendite12M)
    {
        var now = DateTime.Now;

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
        DateTime? ultimaVendita)
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

        var anniFermo =
            Math.Max(
                0,
                (int)Math.Floor(
                    (DateTime.Now - ultimaVendita.Value).TotalDays / 365.25));

        if (anniFermo >= 1)
        {
            return $"FIFO {valoreFifo:0.00} € - nessuna vendita 12M, fermo da circa {anniFermo} anni";
        }

        return $"FIFO {valoreFifo:0.00} € - nessuna vendita negli ultimi 12 mesi";
    }

    private static string GetString(
        SqlDataReader reader,
        string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);

        if (reader.IsDBNull(ordinal))
        {
            return "";
        }

        return reader.GetValue(ordinal)?.ToString()?.Trim() ?? "";
    }

    private static long GetInt64(
        SqlDataReader reader,
        string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);

        if (reader.IsDBNull(ordinal))
        {
            return 0L;
        }

        return Convert.ToInt64(
            reader.GetValue(ordinal),
            CultureInfo.InvariantCulture);
    }

    private static string GetNumberAsString(
        SqlDataReader reader,
        string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);

        if (reader.IsDBNull(ordinal))
        {
            return "";
        }

        var value = Convert.ToDecimal(
            reader.GetValue(ordinal),
            CultureInfo.InvariantCulture);

        return value.ToString(
            "0.#####",
            CultureInfo.InvariantCulture);
    }

    private static decimal? GetNullableDecimal(
        SqlDataReader reader,
        string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal)) return null;
        return Convert.ToDecimal(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }


    private static string GetNullableStockValue(
        SqlDataReader reader,
        string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);

        if (reader.IsDBNull(ordinal))
        {
            return "";
        }

        var value = Convert.ToDecimal(
            reader.GetValue(ordinal),
            CultureInfo.InvariantCulture);

        if (value == -1m)
        {
            return "";
        }

        return value.ToString(
            "0.#####",
            CultureInfo.InvariantCulture);
    }
}
