using Microsoft.Data.SqlClient;
using Scan2EnterGateway.Models;

namespace Scan2EnterGateway.Data;

public sealed class ReorderRepository
{
    private readonly string _connectionString;

    public ReorderRepository(IConfiguration configuration)
    {
        _connectionString =
            configuration.GetConnectionString("DueDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'DueDatabase' is missing.");
    }

    public async Task<string> CheckConnectionAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DB_NAME();";

        var value = await command.ExecuteScalarAsync(ct);
        return Convert.ToString(value) ?? "";
    }

    public async Task<IReadOnlyList<ReorderArticle>> GetReorderListAsync(
        CancellationToken ct)
    {
        const string sql = """
SELECT
    a.idArticolo,
    a.CodiceArticolo,
    a.Descrizione,
    b.Barcode,
    g.idMagazzino,
    g.Giacenza,
    g.Ordinato,
    g.Impegnato,
    g.Disponibile,
    s.ScortaMinima,
    s.ScortaMassima,
    s.LottoRiordino,
    s.NonOrdinabileAFornitore,
    af.IdFornitore,
    af.CodiceArticoloFornitore,
    f.Fornitore AS SupplierName
FROM dbo.tabArticoli AS a
INNER JOIN dbo.TabScortaArticoliView AS s
    ON s.idArticolo = a.idArticolo
INNER JOIN dbo.tabGiacenze AS g
    ON g.idArticolo = a.idArticolo
   AND g.idMagazzino = s.idMagazzino
LEFT JOIN dbo.TabArticoliFornitori AS af
    ON af.IdArticolo = a.idArticolo
   AND af.Predefinito = 1
LEFT JOIN dbo.ListaFornitori AS f
    ON f.ID = af.IdFornitore
OUTER APPLY
(
    SELECT TOP (1) tb.Barcode
    FROM dbo.tabBarcode AS tb
    WHERE tb.idArticolo = a.idArticolo
      AND NULLIF(LTRIM(RTRIM(tb.Barcode)), '') IS NOT NULL
    ORDER BY tb.Barcode
) AS b
WHERE
    s.idMagazzino = @warehouseId
ORDER BY
    a.Descrizione,
    a.CodiceArticolo;
""";

        var items = new List<ReorderArticle>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@warehouseId", 0);
        command.CommandTimeout = 60;

        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var stock = Number(reader, "Giacenza");
            var available = Number(reader, "Disponibile");
            var minimumStock = NormalizeQuantity(Number(reader, "ScortaMinima"));
            var maximumStock = NormalizeQuantity(Number(reader, "ScortaMassima"));
            var reorderLot = NormalizeQuantity(Number(reader, "LottoRiordino"));

            if (!NeedsReorder(
                    stock,
                    available,
                    minimumStock,
                    maximumStock,
                    reorderLot))
            {
                continue;
            }

            var suggestedQuantity = CalculateSuggestedQuantity(
                stock,
                minimumStock,
                reorderLot);

            if (suggestedQuantity <= 0m)
            {
                continue;
            }

            items.Add(new ReorderArticle
            {
                IdArticle = Convert.ToInt32(reader["idArticolo"]),
                ArticleCode = Text(reader, "CodiceArticolo") ?? "",
                Description = Text(reader, "Descrizione") ?? "",
                Barcode = Text(reader, "Barcode"),
                WarehouseId = Convert.ToInt32(reader["idMagazzino"]),
                SupplierId = Integer64(reader, "IdFornitore"),
                SupplierName = Text(reader, "SupplierName") ?? "",
                SupplierArticleCode =
                    Text(reader, "CodiceArticoloFornitore") ?? "",
                Stock = stock,
                Ordered = Number(reader, "Ordinato"),
                Committed = Number(reader, "Impegnato"),
                Available = available,
                MinimumStock = minimumStock,
                MaximumStock = maximumStock,
                ReorderLot = reorderLot,
                NotOrderableFromSupplier = Flag(reader, "NonOrdinabileAFornitore"),
                SuggestedQuantity = suggestedQuantity
            });
        }

        return items;
    }

    private static bool NeedsReorder(
        decimal? stock,
        decimal? available,
        decimal? minimumStock,
        decimal? maximumStock,
        decimal? reorderLot)
    {
        if (stock is null || available is null)
        {
            return false;
        }

        var excludedFromAutomaticReorder =
            minimumStock is null &&
            maximumStock is null &&
            reorderLot is null;

        if (excludedFromAutomaticReorder)
        {
            return false;
        }

        var reorderByLotWithoutMinimum =
            minimumStock is null &&
            reorderLot is > 0m;

        if (reorderByLotWithoutMinimum)
        {
            return true;
        }

        if (minimumStock is null)
        {
            return false;
        }

        return
            stock.Value <= minimumStock.Value ||
            available.Value <= 0m;
    }

    private static decimal CalculateSuggestedQuantity(
        decimal? stock,
        decimal? minimumStock,
        decimal? reorderLot)
    {
        if (reorderLot is > 0m)
        {
            return reorderLot.Value;
        }

        if (stock is not null && minimumStock is not null)
        {
            return Math.Max(0m, minimumStock.Value - stock.Value);
        }

        return 0m;
    }

    private static decimal? NormalizeQuantity(decimal? value)
    {
        return value == -1m ? null : value;
    }

    private static string? Text(SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);

        return reader.IsDBNull(ordinal)
            ? null
            : Convert.ToString(reader.GetValue(ordinal))?.Trim();
    }

    private static long Integer64(SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);

        return reader.IsDBNull(ordinal)
            ? 0L
            : Convert.ToInt64(reader.GetValue(ordinal));
    }

    private static decimal? Number(SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);

        return reader.IsDBNull(ordinal)
            ? null
            : Convert.ToDecimal(reader.GetValue(ordinal));
    }

    private static bool? Flag(SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);

        return reader.IsDBNull(ordinal)
            ? null
            : Convert.ToInt32(reader.GetValue(ordinal)) != 0;
    }
}