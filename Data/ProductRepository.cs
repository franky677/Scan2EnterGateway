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