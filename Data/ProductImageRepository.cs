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
                percorsoImmagine
            FROM dbo.tabImmaginiProdotti
            WHERE idArticolo = @articleId
              AND NULLIF(LTRIM(RTRIM(percorsoImmagine)), '') IS NOT NULL
            ORDER BY
                CASE
                    WHEN ISNULL(idVariante1, -1) = -1
                     AND ISNULL(idVariante2, -1) = -1
                     AND ISNULL(idVariante3, -1) = -1
                    THEN 0
                    ELSE 1
                END,
                idImmagine DESC;
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