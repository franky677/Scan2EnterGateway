using System.Globalization;
using Microsoft.Data.SqlClient;
using Scan2EnterGateway.Models;

namespace Scan2EnterGateway.Data;

public sealed class ProductExpiryRepository
{
    private readonly string _connectionString;
    private readonly int _warehouseId;

    public ProductExpiryRepository(IConfiguration configuration)
    {
        _connectionString =
            configuration.GetConnectionString("DueDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'DueDatabase' is missing.");

        _warehouseId =
            configuration.GetValue<int?>("Gateway:WarehouseId") ?? 0;
    }

    private async Task EnsureTableAsync(
        SqlConnection connection,
        CancellationToken ct)
    {
        const string sql = """
            IF OBJECT_ID(N'dbo.Scan2EnterProductExpiry', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.Scan2EnterProductExpiry
                (
                    IdArticolo bigint NOT NULL,
                    MeseScadenza tinyint NOT NULL,
                    AnnoScadenza smallint NOT NULL,
                    DataAggiornamento datetime2(0) NOT NULL
                        CONSTRAINT DF_Scan2EnterProductExpiry_DataAggiornamento
                        DEFAULT SYSDATETIME(),

                    CONSTRAINT PK_Scan2EnterProductExpiry
                        PRIMARY KEY (IdArticolo),

                    CONSTRAINT CK_Scan2EnterProductExpiry_Mese
                        CHECK (MeseScadenza BETWEEN 1 AND 12),

                    CONSTRAINT CK_Scan2EnterProductExpiry_Anno
                        CHECK (AnnoScadenza BETWEEN 2000 AND 2200)
                );

                CREATE INDEX IX_Scan2EnterProductExpiry_Data
                    ON dbo.Scan2EnterProductExpiry
                    (
                        AnnoScadenza,
                        MeseScadenza
                    );
            END;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = 30;
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<ProductExpiryDto?> GetAsync(
        long articleId,
        CancellationToken ct = default)
    {
        await using var connection =
            new SqlConnection(_connectionString);

        await connection.OpenAsync(ct);
        await EnsureTableAsync(connection, ct);

        const string sql = """
            SELECT
                a.idArticolo,
                a.CodiceArticolo,
                a.Descrizione,
                barcode.Barcode,
                e.MeseScadenza,
                e.AnnoScadenza,
                DATEADD(
                    DAY,
                    -1,
                    DATEADD(
                        MONTH,
                        1,
                        DATEADD(
                            MONTH,
                            e.MeseScadenza - 1,
                            DATEADD(
                                YEAR,
                                e.AnnoScadenza - 1900,
                                0
                            )
                        )
                    )
                ) AS DataScadenza,
                CASE
                    WHEN DATEADD(
                        DAY,
                        -1,
                        DATEADD(
                            MONTH,
                            1,
                            DATEADD(
                                MONTH,
                                e.MeseScadenza - 1,
                                DATEADD(
                                    YEAR,
                                    e.AnnoScadenza - 1900,
                                    0
                                )
                            )
                        )
                    ) < DATEADD(DAY, DATEDIFF(DAY, 0, GETDATE()), 0)
                    THEN CAST(1 AS bit)
                    ELSE CAST(0 AS bit)
                END AS Scaduto,
                DATEDIFF(
                    DAY,
                    DATEADD(DAY, DATEDIFF(DAY, 0, GETDATE()), 0),
                    DATEADD(
                        DAY,
                        -1,
                        DATEADD(
                            MONTH,
                            1,
                            DATEADD(
                                MONTH,
                                e.MeseScadenza - 1,
                                DATEADD(
                                    YEAR,
                                    e.AnnoScadenza - 1900,
                                    0
                                )
                            )
                        )
                    )
                ) AS GiorniAllaScadenza,
                ISNULL(g.Giacenza, 0) AS Giacenza,
                e.DataAggiornamento
            FROM dbo.Scan2EnterProductExpiry AS e
            INNER JOIN dbo.tabArticoli AS a
                ON a.idArticolo = e.IdArticolo
            LEFT JOIN dbo.tabGiacenze AS g
                ON g.idArticolo = a.idArticolo
               AND g.idMagazzino = @warehouseId
            OUTER APPLY
            (
                SELECT TOP (1)
                    LTRIM(RTRIM(b.Barcode)) AS Barcode
                FROM dbo.tabBarcode AS b
                WHERE b.idArticolo = a.idArticolo
                  AND NULLIF(LTRIM(RTRIM(b.Barcode)), '') IS NOT NULL
                ORDER BY
                    CASE
                        WHEN LEN(LTRIM(RTRIM(b.Barcode))) = 13 THEN 0
                        ELSE 1
                    END,
                    b.Barcode
            ) AS barcode
            WHERE e.IdArticolo = @articleId;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@articleId", articleId);
        command.Parameters.AddWithValue("@warehouseId", _warehouseId);
        command.CommandTimeout = 30;

        await using var reader =
            await command.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return ReadExpiry(reader);
    }

    public async Task<ProductExpiryDto?> UpsertAsync(
        long articleId,
        int month,
        int year,
        CancellationToken ct = default)
    {
        await using var connection =
            new SqlConnection(_connectionString);

        await connection.OpenAsync(ct);
        await EnsureTableAsync(connection, ct);

        const string articleExistsSql = """
            SELECT COUNT_BIG(1)
            FROM dbo.tabArticoli
            WHERE idArticolo = @articleId;
            """;

        await using (var existsCommand =
            new SqlCommand(articleExistsSql, connection))
        {
            existsCommand.Parameters.AddWithValue(
                "@articleId",
                articleId);

            var exists =
                Convert.ToInt64(
                    await existsCommand.ExecuteScalarAsync(ct),
                    CultureInfo.InvariantCulture) > 0;

            if (!exists)
            {
                return null;
            }
        }

        const string upsertSql = """
            UPDATE dbo.Scan2EnterProductExpiry
            SET
                MeseScadenza = @month,
                AnnoScadenza = @year,
                DataAggiornamento = SYSDATETIME()
            WHERE IdArticolo = @articleId;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO dbo.Scan2EnterProductExpiry
                (
                    IdArticolo,
                    MeseScadenza,
                    AnnoScadenza,
                    DataAggiornamento
                )
                VALUES
                (
                    @articleId,
                    @month,
                    @year,
                    SYSDATETIME()
                );
            END;
            """;

        await using (var command =
            new SqlCommand(upsertSql, connection))
        {
            command.Parameters.AddWithValue("@articleId", articleId);
            command.Parameters.AddWithValue("@month", month);
            command.Parameters.AddWithValue("@year", year);
            command.CommandTimeout = 30;
            await command.ExecuteNonQueryAsync(ct);
        }

        return await GetAsync(articleId, ct);
    }

    public async Task<bool> DeleteAsync(
        long articleId,
        CancellationToken ct = default)
    {
        await using var connection =
            new SqlConnection(_connectionString);

        await connection.OpenAsync(ct);
        await EnsureTableAsync(connection, ct);

        const string sql = """
            DELETE FROM dbo.Scan2EnterProductExpiry
            WHERE IdArticolo = @articleId;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@articleId", articleId);
        command.CommandTimeout = 30;

        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<ProductExpiryAlertsDto> GetAlertsAsync(
        int withinMonths,
        CancellationToken ct = default)
    {
        await using var connection =
            new SqlConnection(_connectionString);

        await connection.OpenAsync(ct);
        await EnsureTableAsync(connection, ct);

        const string sql = """
            SELECT
                a.idArticolo,
                a.CodiceArticolo,
                a.Descrizione,
                barcode.Barcode,
                e.MeseScadenza,
                e.AnnoScadenza,
                DATEADD(
                    DAY,
                    -1,
                    DATEADD(
                        MONTH,
                        1,
                        DATEADD(
                            MONTH,
                            e.MeseScadenza - 1,
                            DATEADD(
                                YEAR,
                                e.AnnoScadenza - 1900,
                                0
                            )
                        )
                    )
                ) AS DataScadenza,
                CASE
                    WHEN DATEADD(
                        DAY,
                        -1,
                        DATEADD(
                            MONTH,
                            1,
                            DATEADD(
                                MONTH,
                                e.MeseScadenza - 1,
                                DATEADD(
                                    YEAR,
                                    e.AnnoScadenza - 1900,
                                    0
                                )
                            )
                        )
                    ) < DATEADD(DAY, DATEDIFF(DAY, 0, GETDATE()), 0)
                    THEN CAST(1 AS bit)
                    ELSE CAST(0 AS bit)
                END AS Scaduto,
                DATEDIFF(
                    DAY,
                    DATEADD(DAY, DATEDIFF(DAY, 0, GETDATE()), 0),
                    DATEADD(
                        DAY,
                        -1,
                        DATEADD(
                            MONTH,
                            1,
                            DATEADD(
                                MONTH,
                                e.MeseScadenza - 1,
                                DATEADD(
                                    YEAR,
                                    e.AnnoScadenza - 1900,
                                    0
                                )
                            )
                        )
                    )
                ) AS GiorniAllaScadenza,
                ISNULL(g.Giacenza, 0) AS Giacenza,
                e.DataAggiornamento
            FROM dbo.Scan2EnterProductExpiry AS e
            INNER JOIN dbo.tabArticoli AS a
                ON a.idArticolo = e.IdArticolo
            LEFT JOIN dbo.tabGiacenze AS g
                ON g.idArticolo = a.idArticolo
               AND g.idMagazzino = @warehouseId
            OUTER APPLY
            (
                SELECT TOP (1)
                    LTRIM(RTRIM(b.Barcode)) AS Barcode
                FROM dbo.tabBarcode AS b
                WHERE b.idArticolo = a.idArticolo
                  AND NULLIF(LTRIM(RTRIM(b.Barcode)), '') IS NOT NULL
                ORDER BY
                    CASE
                        WHEN LEN(LTRIM(RTRIM(b.Barcode))) = 13 THEN 0
                        ELSE 1
                    END,
                    b.Barcode
            ) AS barcode
            WHERE
                DATEADD(
                    DAY,
                    -1,
                    DATEADD(
                        MONTH,
                        1,
                        DATEADD(
                            MONTH,
                            e.MeseScadenza - 1,
                            DATEADD(
                                YEAR,
                                e.AnnoScadenza - 1900,
                                0
                            )
                        )
                    )
                )
                <= DATEADD(
                    DAY,
                    -1,
                    DATEADD(
                        MONTH,
                        DATEDIFF(
                            MONTH,
                            0,
                            DATEADD(
                                MONTH,
                                @withinMonths + 1,
                                GETDATE()
                            )
                        ),
                        0
                    )
                )
            ORDER BY
                DataScadenza,
                a.Descrizione,
                a.idArticolo;
            """;

        var items = new List<ProductExpiryDto>();

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue(
            "@withinMonths",
            Math.Clamp(withinMonths, 0, 24));
        command.Parameters.AddWithValue(
            "@warehouseId",
            _warehouseId);
        command.CommandTimeout = 30;

        await using var reader =
            await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            items.Add(ReadExpiry(reader));
        }

        return new ProductExpiryAlertsDto
        {
            WithinMonths = Math.Clamp(withinMonths, 0, 24),
            ExpiredCount = items.Count(x => x.IsExpired),
            ExpiringCount = items.Count(x => !x.IsExpired),
            TotalCount = items.Count,
            GeneratedAt = DateTime.Now,
            Items = items
        };
    }

    private static ProductExpiryDto ReadExpiry(
        SqlDataReader reader)
    {
        return new ProductExpiryDto
        {
            ArticleId = Convert.ToInt64(
                reader["idArticolo"],
                CultureInfo.InvariantCulture),

            ArticleCode =
                reader["CodiceArticolo"]?.ToString()?.Trim() ?? "",

            Description =
                reader["Descrizione"]?.ToString()?.Trim() ?? "",

            Barcode =
                reader["Barcode"]?.ToString()?.Trim() ?? "",

            Month = Convert.ToInt32(
                reader["MeseScadenza"],
                CultureInfo.InvariantCulture),

            Year = Convert.ToInt32(
                reader["AnnoScadenza"],
                CultureInfo.InvariantCulture),

            ExpiryDate = Convert.ToDateTime(
                reader["DataScadenza"],
                CultureInfo.InvariantCulture),

            IsExpired = Convert.ToBoolean(
                reader["Scaduto"],
                CultureInfo.InvariantCulture),

            DaysToExpiry = Convert.ToInt32(
                reader["GiorniAllaScadenza"],
                CultureInfo.InvariantCulture),

            Stock = Convert.ToDecimal(
                reader["Giacenza"],
                CultureInfo.InvariantCulture),

            UpdatedAt = Convert.ToDateTime(
                reader["DataAggiornamento"],
                CultureInfo.InvariantCulture)
        };
    }
}
