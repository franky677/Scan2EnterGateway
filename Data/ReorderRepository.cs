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

    private static async Task EnsureSupplierSelectionTableAsync(
        SqlConnection connection,
        CancellationToken ct)
    {
        const string sql = """
IF OBJECT_ID('dbo.Scan2EnterReorderSupplier', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Scan2EnterReorderSupplier
    (
        IdArticolo int NOT NULL,
        IdMagazzino int NOT NULL,
        IdFornitoreScelto int NOT NULL,
        DataAgg datetime NOT NULL DEFAULT GETDATE(),
        CONSTRAINT PK_Scan2EnterReorderSupplier
            PRIMARY KEY (IdArticolo, IdMagazzino)
    );
END;
""";

        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<ReorderArticle>> GetReorderListAsync(
        CancellationToken ct)
    {
        const int warehouseId = 0;

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

    af.IdFornitore AS IdFornitore,
    af.CodiceArticoloFornitore AS CodiceArticoloFornitore,
    COALESCE(f.Fornitore, '') AS SupplierName,

    pa.Imponibile AS PurchaseTaxable,
    pa.PrezzoAcquisto AS PurchasePrice,
    pa.aIva AS VatRate

FROM dbo.tabArticoli AS a
INNER JOIN dbo.TabScortaArticoliView AS s
    ON s.idArticolo = a.idArticolo
INNER JOIN dbo.tabGiacenze AS g
    ON g.idArticolo = a.idArticolo
   AND g.idMagazzino = s.idMagazzino

/*
 * MULTI-FORNITORE:
 * una stessa necessità di riordino viene esposta una volta per ogni
 * fornitore associato all'articolo. Il fabbisogno resta dell'articolo:
 * quando NeedsReorder() diventa false non viene restituita nessuna riga,
 * quindi l'articolo scompare contemporaneamente da tutti i fornitori.
 */
LEFT JOIN dbo.TabArticoliFornitori AS af
    ON af.IdArticolo = a.idArticolo

LEFT JOIN dbo.ListaFornitori AS f
    ON f.ID = af.IdFornitore

LEFT JOIN dbo.TabPrezziAcquisto AS pa
    ON pa.idFornitore = af.IdFornitore
   AND pa.CodiceArticoloFornitore = af.CodiceArticoloFornitore
   AND ISNULL(pa.idVariante1, -1) = -1
   AND ISNULL(pa.idVariante2, -1) = -1
   AND ISNULL(pa.idVariante3, -1) = -1

OUTER APPLY
(
    SELECT TOP (1) tb.Barcode
    FROM dbo.tabBarcode AS tb
    WHERE tb.idArticolo = a.idArticolo
      AND NULLIF(LTRIM(RTRIM(tb.Barcode)), '') IS NOT NULL
    ORDER BY tb.Barcode
) AS b

WHERE s.idMagazzino = @warehouseId

ORDER BY
    COALESCE(f.Fornitore, ''),
    a.Descrizione,
    a.CodiceArticolo,
    af.IdFornitore;
""";

        var items = new List<ReorderArticle>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        await EnsureSupplierSelectionTableAsync(connection, ct);

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@warehouseId", warehouseId);
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
                PurchaseTaxable = Number(reader, "PurchaseTaxable"),
                PurchasePrice = Number(reader, "PurchasePrice"),
                VatRate = Number(reader, "VatRate"),
                Stock = stock,
                Ordered = Number(reader, "Ordinato"),
                Committed = Number(reader, "Impegnato"),
                Available = available,
                MinimumStock = minimumStock,
                MaximumStock = maximumStock,
                ReorderLot = reorderLot,
                NotOrderableFromSupplier =
                    Flag(reader, "NonOrdinabileAFornitore"),
                SuggestedQuantity = suggestedQuantity
            });
        }

        await reader.DisposeAsync();

        return items;
    }

    public async Task<IReadOnlyList<ReorderSupplierOption>> GetReorderSuppliersAsync(
        int articleId,
        int warehouseId,
        CancellationToken ct)
    {
        const string sql = """
SELECT
    af.IdFornitore,
    f.Fornitore AS SupplierName,
    af.CodiceArticoloFornitore,
    af.Predefinito,
    CASE
        WHEN sel.IdFornitoreScelto = af.IdFornitore THEN 1
        ELSE 0
    END AS Selected,
    pa.Imponibile,
    pa.Sconto1,
    pa.Sconto2,
    pa.Sconto3,
    pa.Sconto4,
    pa.PrezzoAcquisto,
    pa.aIva,
    pa.dataAgg
FROM dbo.TabArticoliFornitori AS af
LEFT JOIN dbo.ListaFornitori AS f
    ON f.ID = af.IdFornitore
LEFT JOIN dbo.Scan2EnterReorderSupplier AS sel
    ON sel.IdArticolo = af.IdArticolo
   AND sel.IdMagazzino = @warehouseId
LEFT JOIN dbo.TabPrezziAcquisto AS pa
    ON pa.idFornitore = af.IdFornitore
   AND pa.CodiceArticoloFornitore = af.CodiceArticoloFornitore
   AND ISNULL(pa.idVariante1, -1) = -1
   AND ISNULL(pa.idVariante2, -1) = -1
   AND ISNULL(pa.idVariante3, -1) = -1
WHERE af.IdArticolo = @articleId
ORDER BY
    CASE WHEN sel.IdFornitoreScelto = af.IdFornitore THEN 0 ELSE 1 END,
    af.Predefinito DESC,
    f.Fornitore,
    af.IdFornitore;
""";

        var result = new List<ReorderSupplierOption>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await EnsureSupplierSelectionTableAsync(connection, ct);

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@articleId", articleId);
        command.Parameters.AddWithValue("@warehouseId", warehouseId);

        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var taxable = Number(reader, "Imponibile");
            var net = taxable;

            if (net is not null)
            {
                foreach (var field in new[] { "Sconto1", "Sconto2", "Sconto3", "Sconto4" })
                {
                    var discount = Number(reader, field) ?? 0m;
                    if (discount != 0m)
                    {
                        net *= 1m - (discount / 100m);
                    }
                }
            }

            result.Add(new ReorderSupplierOption
            {
                SupplierId = Integer64(reader, "IdFornitore"),
                SupplierName = Text(reader, "SupplierName") ?? "",
                SupplierArticleCode =
                    Text(reader, "CodiceArticoloFornitore") ?? "",
                IsDefault = Flag(reader, "Predefinito") == true,
                IsSelected = Flag(reader, "Selected") == true,
                PurchaseTaxable = taxable,
                NetPurchaseTaxable = net,
                PurchasePrice = Number(reader, "PrezzoAcquisto"),
                VatRate = Number(reader, "aIva"),
                UpdatedAt =
                    reader.IsDBNull(reader.GetOrdinal("dataAgg"))
                        ? null
                        : reader.GetDateTime(reader.GetOrdinal("dataAgg"))
            });
        }

        if (!result.Any(x => x.IsSelected))
        {
            var defaultIndex = result.FindIndex(x => x.IsDefault);
            if (defaultIndex >= 0)
            {
                result[defaultIndex] =
                    result[defaultIndex] with { IsSelected = true };
            }
        }

        return result;
    }

    public async Task SetReorderSupplierAsync(
        int articleId,
        int warehouseId,
        int supplierId,
        CancellationToken ct)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await EnsureSupplierSelectionTableAsync(connection, ct);

        const string validateSql = """
SELECT COUNT(*)
FROM dbo.TabArticoliFornitori
WHERE IdArticolo = @articleId
  AND IdFornitore = @supplierId;
""";

        await using (var validate =
                     new SqlCommand(validateSql, connection))
        {
            validate.Parameters.AddWithValue("@articleId", articleId);
            validate.Parameters.AddWithValue("@supplierId", supplierId);

            var count = Convert.ToInt32(
                await validate.ExecuteScalarAsync(ct));

            if (count <= 0)
            {
                throw new InvalidOperationException(
                    $"Il fornitore {supplierId} non è associato " +
                    $"all'articolo {articleId}.");
            }
        }

        const string upsertSql = """
IF EXISTS
(
    SELECT 1
    FROM dbo.Scan2EnterReorderSupplier
    WHERE IdArticolo = @articleId
      AND IdMagazzino = @warehouseId
)
BEGIN
    UPDATE dbo.Scan2EnterReorderSupplier
    SET
        IdFornitoreScelto = @supplierId,
        DataAgg = GETDATE()
    WHERE IdArticolo = @articleId
      AND IdMagazzino = @warehouseId;
END
ELSE
BEGIN
    INSERT INTO dbo.Scan2EnterReorderSupplier
    (
        IdArticolo,
        IdMagazzino,
        IdFornitoreScelto,
        DataAgg
    )
    VALUES
    (
        @articleId,
        @warehouseId,
        @supplierId,
        GETDATE()
    );
END;
""";

        await using var command =
            new SqlCommand(upsertSql, connection);

        command.Parameters.AddWithValue("@articleId", articleId);
        command.Parameters.AddWithValue("@warehouseId", warehouseId);
        command.Parameters.AddWithValue("@supplierId", supplierId);

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task ClearReorderSupplierAsync(
        int articleId,
        int warehouseId,
        CancellationToken ct)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await EnsureSupplierSelectionTableAsync(connection, ct);

        const string sql = """
DELETE FROM dbo.Scan2EnterReorderSupplier
WHERE IdArticolo = @articleId
  AND IdMagazzino = @warehouseId;
""";

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@articleId", articleId);
        command.Parameters.AddWithValue("@warehouseId", warehouseId);

        await command.ExecuteNonQueryAsync(ct);
    }


    public async Task UpdateStockSettingsAsync(
        int articleId,
        int warehouseId,
        int variant1Id,
        int variant2Id,
        int variant3Id,
        decimal? minimumStock,
        decimal? maximumStock,
        decimal? reorderLot,
        CancellationToken ct)
    {
        static decimal DbValue(decimal? value) => value ?? -1m;

        const string sql = """
SET XACT_ABORT ON;

UPDATE dbo.tabArticoli
SET
    ScortaMinima = @minimumStock,
    ScortaMassima = @maximumStock,
    LottoRiordino = @reorderLot
WHERE IdArticolo = @articleId;

UPDATE dbo.tabArticoli_Varianti
SET
    ScortaMinima = @minimumStock,
    ScortaMassima = @maximumStock,
    LottoRiordino = @reorderLot
WHERE IdArticolo = @articleId
  AND ISNULL(IdVariante1, -1) = @variant1Id
  AND ISNULL(IdVariante2, -1) = @variant2Id
  AND ISNULL(IdVariante3, -1) = @variant3Id;

IF EXISTS
(
    SELECT 1
    FROM dbo.tabScortaArticoli
    WHERE IdArticolo = @articleId
      AND IdMagazzino = @warehouseId
      AND IdVariante1 = @variant1Id
      AND IdVariante2 = @variant2Id
      AND IdVariante3 = @variant3Id
)
BEGIN
    UPDATE dbo.tabScortaArticoli
    SET
        ScortaMinima = @minimumStock,
        ScortaMassima = @maximumStock,
        LottoRiordino = @reorderLot
    WHERE IdArticolo = @articleId
      AND IdMagazzino = @warehouseId
      AND IdVariante1 = @variant1Id
      AND IdVariante2 = @variant2Id
      AND IdVariante3 = @variant3Id;
END
ELSE
BEGIN
    INSERT INTO dbo.tabScortaArticoli
    (
        IdArticolo,
        IdMagazzino,
        IdVariante1,
        IdVariante2,
        IdVariante3,
        ScortaMinima,
        ScortaMassima,
        LottoRiordino
    )
    VALUES
    (
        @articleId,
        @warehouseId,
        @variant1Id,
        @variant2Id,
        @variant3Id,
        @minimumStock,
        @maximumStock,
        @reorderLot
    );
END;
""";

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        try
        {
            await using var command = new SqlCommand(sql, connection, (SqlTransaction)transaction);
            command.Parameters.AddWithValue("@articleId", articleId);
            command.Parameters.AddWithValue("@warehouseId", warehouseId);
            command.Parameters.AddWithValue("@variant1Id", variant1Id);
            command.Parameters.AddWithValue("@variant2Id", variant2Id);
            command.Parameters.AddWithValue("@variant3Id", variant3Id);
            command.Parameters.AddWithValue("@minimumStock", DbValue(minimumStock));
            command.Parameters.AddWithValue("@maximumStock", DbValue(maximumStock));
            command.Parameters.AddWithValue("@reorderLot", DbValue(reorderLot));

            var affectedRows = await command.ExecuteNonQueryAsync(ct);

            if (affectedRows <= 0)
            {
                throw new InvalidOperationException(
                    $"Nessun record aggiornato per l'articolo {articleId}.");
            }

            /*
             * La scelta fornitore vale per il ciclo di riordino corrente.
             * La azzeriamo solo quando una modifica di scorta rende davvero
             * l'articolo non più da riordinare.
             */
            const string reorderStateSql = """
SELECT
    g.Giacenza,
    g.Disponibile,
    s.ScortaMinima,
    s.ScortaMassima,
    s.LottoRiordino
FROM dbo.TabScortaArticoliView AS s
INNER JOIN dbo.tabGiacenze AS g
    ON g.idArticolo = s.idArticolo
   AND g.idMagazzino = s.idMagazzino
WHERE s.idArticolo = @articleId
  AND s.idMagazzino = @warehouseId;
""";

            await using var stateCommand =
                new SqlCommand(
                    reorderStateSql,
                    connection,
                    (SqlTransaction)transaction);

            stateCommand.Parameters.AddWithValue(
                "@articleId",
                articleId);

            stateCommand.Parameters.AddWithValue(
                "@warehouseId",
                warehouseId);

            await using var stateReader =
                await stateCommand.ExecuteReaderAsync(ct);

            var stillNeedsReorder = false;

            if (await stateReader.ReadAsync(ct))
            {
                var stock = Number(stateReader, "Giacenza");
                var available = Number(stateReader, "Disponibile");
                var min = NormalizeQuantity(
                    Number(stateReader, "ScortaMinima"));
                var max = NormalizeQuantity(
                    Number(stateReader, "ScortaMassima"));
                var lot = NormalizeQuantity(
                    Number(stateReader, "LottoRiordino"));

                stillNeedsReorder =
                    NeedsReorder(
                        stock,
                        available,
                        min,
                        max,
                        lot);
            }

            await stateReader.DisposeAsync();

            if (!stillNeedsReorder)
            {
                const string clearSupplierSql = """
DELETE FROM dbo.Scan2EnterReorderSupplier
WHERE IdArticolo = @articleId
  AND IdMagazzino = @warehouseId;
""";

                await using var clearSupplier =
                    new SqlCommand(
                        clearSupplierSql,
                        connection,
                        (SqlTransaction)transaction);

                clearSupplier.Parameters.AddWithValue(
                    "@articleId",
                    articleId);

                clearSupplier.Parameters.AddWithValue(
                    "@warehouseId",
                    warehouseId);

                await clearSupplier.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
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