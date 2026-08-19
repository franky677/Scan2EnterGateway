using Microsoft.Data.Sqlite;

namespace Scan2EnterGateway.Data;

public sealed record FavoriteRecord(
    long ArticleId,
    string Barcode,
    string ArticleCode,
    string Description,
    string PublicPrice,
    string Stock,
    string AddedAtUtc);

public sealed record FavoriteUpsertRequest(
    long ArticleId,
    string Barcode = "",
    string ArticleCode = "",
    string Description = "",
    string PublicPrice = "",
    string Stock = "");

public sealed class FavoriteRepository
{
    private const string DataDirectory = @"C:\Scan2EnterData";
    private static readonly string DatabasePath =
        Path.Combine(DataDirectory, "Scan2Enter.db");

    private readonly ILogger<FavoriteRepository> _logger;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;

    public FavoriteRepository(ILogger<FavoriteRepository> logger)
    {
        _logger = logger;
    }

    public async Task<List<FavoriteRecord>> GetAllAsync(
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        var result = new List<FavoriteRecord>();

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                ArticleId,
                COALESCE(Barcode, ''),
                COALESCE(ArticleCode, ''),
                COALESCE(Description, ''),
                COALESCE(PublicPrice, ''),
                COALESCE(Stock, ''),
                AddedAtUtc
            FROM Favorites
            ORDER BY AddedAtUtc ASC, ArticleId ASC;
            """;

        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            result.Add(
                new FavoriteRecord(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6)));
        }

        return result;
    }

    public async Task<bool> UpsertAsync(
        FavoriteUpsertRequest request,
        CancellationToken ct = default)
    {
        if (request.ArticleId <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.ArticleId));

        await EnsureInitializedAsync(ct);

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO Favorites
            (
                ArticleId, Barcode, ArticleCode, Description,
                PublicPrice, Stock, AddedAtUtc
            )
            VALUES
            (
                $articleId, $barcode, $articleCode, $description,
                $publicPrice, $stock, $addedAtUtc
            )
            ON CONFLICT(ArticleId) DO UPDATE SET
                Barcode = excluded.Barcode,
                ArticleCode = excluded.ArticleCode,
                Description = excluded.Description,
                PublicPrice = excluded.PublicPrice,
                Stock = excluded.Stock;
            """;

        command.Parameters.AddWithValue("$articleId", request.ArticleId);
        command.Parameters.AddWithValue("$barcode", request.Barcode?.Trim() ?? "");
        command.Parameters.AddWithValue("$articleCode", request.ArticleCode?.Trim() ?? "");
        command.Parameters.AddWithValue("$description", request.Description?.Trim() ?? "");
        command.Parameters.AddWithValue("$publicPrice", request.PublicPrice?.Trim() ?? "");
        command.Parameters.AddWithValue("$stock", request.Stock?.Trim() ?? "");
        command.Parameters.AddWithValue("$addedAtUtc", DateTimeOffset.UtcNow.ToString("O"));

        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> RemoveAsync(
        long articleId,
        CancellationToken ct = default)
    {
        if (articleId <= 0) return false;

        await EnsureInitializedAsync(ct);

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM Favorites
            WHERE ArticleId = $articleId;
            """;

        command.Parameters.AddWithValue("$articleId", articleId);

        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    private SqliteConnection CreateConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };

        return new SqliteConnection(builder.ToString());
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        await _initializeLock.WaitAsync(ct);

        try
        {
            if (_initialized) return;

            Directory.CreateDirectory(DataDirectory);

            await using var connection = CreateConnection();
            await connection.OpenAsync(ct);

            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    CREATE TABLE IF NOT EXISTS Favorites
                    (
                        ArticleId INTEGER NOT NULL PRIMARY KEY,
                        AddedAtUtc TEXT NOT NULL,
                        Barcode TEXT NOT NULL DEFAULT '',
                        ArticleCode TEXT NOT NULL DEFAULT '',
                        Description TEXT NOT NULL DEFAULT '',
                        PublicPrice TEXT NOT NULL DEFAULT '',
                        Stock TEXT NOT NULL DEFAULT ''
                    );
                    """;

                await command.ExecuteNonQueryAsync(ct);
            }

            foreach (var columnSql in new[]
            {
                "ALTER TABLE Favorites ADD COLUMN Barcode TEXT NOT NULL DEFAULT '';",
                "ALTER TABLE Favorites ADD COLUMN ArticleCode TEXT NOT NULL DEFAULT '';",
                "ALTER TABLE Favorites ADD COLUMN Description TEXT NOT NULL DEFAULT '';",
                "ALTER TABLE Favorites ADD COLUMN PublicPrice TEXT NOT NULL DEFAULT '';",
                "ALTER TABLE Favorites ADD COLUMN Stock TEXT NOT NULL DEFAULT '';"
            })
            {
                try
                {
                    await using var alter = connection.CreateCommand();
                    alter.CommandText = columnSql;
                    await alter.ExecuteNonQueryAsync(ct);
                }
                catch (SqliteException ex)
                {
                    if (!ex.Message.Contains(
                        "duplicate column name",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        throw;
                    }
                }
            }

            _initialized = true;

            _logger.LogInformation(
                "Database Scan2Enter Preferiti inizializzato: {DatabasePath}",
                DatabasePath);
        }
        finally
        {
            _initializeLock.Release();
        }
    }
}