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
    s.NonOrdinabileAFornitore
FROM dbo.tabArticoli AS a
INNER JOIN dbo.TabScortaArticoliView AS s
    ON s.idArticolo = a.idArticolo
INNER JOIN dbo.tabGiacenze AS g
    ON g.idArticolo = a.idArticolo
   AND g.idMagazzino = s.idMagazzino
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
    AND s.ScortaMinima IS NOT NULL
    AND g.Giacenza <= s.ScortaMinima
ORDER BY
    a.Descrizione,
    a.CodiceArticolo;
""";

        var items = new List<ReorderArticle>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@warehouseId", 0);
        command.CommandTimeout = 30;

        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            items.Add(new ReorderArticle
            {
                IdArticle = Convert.ToInt32(reader["idArticolo"]),
                ArticleCode = Text(reader, "CodiceArticolo") ?? "",
                Description = Text(reader, "Descrizione") ?? "",
                Barcode = Text(reader, "Barcode"),
                WarehouseId = Convert.ToInt32(reader["idMagazzino"]),
                Stock = Number(reader, "Giacenza"),
                Ordered = Number(reader, "Ordinato"),
                Committed = Number(reader, "Impegnato"),
                Available = Number(reader, "Disponibile"),
                MinimumStock = Number(reader, "ScortaMinima") ?? 0m,
                MaximumStock = Number(reader, "ScortaMassima"),
                ReorderLot = Number(reader, "LottoRiordino"),
                NotOrderableFromSupplier = Flag(reader, "NonOrdinabileAFornitore")
            });
        }

        return items;
    }

    private static string? Text(SqlDataReader r, string name)
    {
        var i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i))?.Trim();
    }

    private static decimal? Number(SqlDataReader r, string name)
    {
        var i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? null : Convert.ToDecimal(r.GetValue(i));
    }

    private static bool? Flag(SqlDataReader r, string name)
    {
        var i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? null : Convert.ToInt32(r.GetValue(i)) != 0;
    }
}
