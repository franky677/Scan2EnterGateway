using Microsoft.Data.SqlClient;

namespace Scan2EnterGateway.Data;

public sealed class ProductImageRepository
{
    private readonly string _connectionString;

    public ProductImageRepository(IConfiguration configuration)
    {
        _connectionString =
            configuration.GetConnectionString("DueDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'DueDatabase' is missing.");
    }

    public async Task<string?> GetImagePathByArticleIdAsync(
        long articleId,
        CancellationToken cancellationToken = default)
    {
        if (articleId <= 0)
        {
            return null;
        }

        const string sql = """
            SELECT TOP (1)
                ImagePath
            FROM
            (
                SELECT
                    0 AS Priority,
                    NULLIF(LTRIM(RTRIM(a.Immagine)), '') AS ImagePath
                FROM dbo.tabArticoli a
                WHERE a.idArticolo = @articleId

                UNION ALL

                SELECT
                    1 AS Priority,
                    NULLIF(LTRIM(RTRIM(i.percorsoImmagine)), '') AS ImagePath
                FROM dbo.tabImmaginiProdotti i
                WHERE i.idArticolo = @articleId
            ) Images
            WHERE ImagePath IS NOT NULL
            ORDER BY Priority;
            """;

        await using var connection =
            new SqlConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);

        await using var command =
            new SqlCommand(sql, connection);

        command.CommandTimeout = 30;

        command.Parameters.Add(
            "@articleId",
            System.Data.SqlDbType.BigInt
        ).Value = articleId;

        var result =
            await command.ExecuteScalarAsync(cancellationToken);

        if (result is null || result == DBNull.Value)
        {
            return null;
        }

        var path = result.ToString()?.Trim();

        return string.IsNullOrWhiteSpace(path)
            ? null
            : path;
    }
}