using Microsoft.Data.SqlClient;
using Scan2EnterGateway.Models;

namespace Scan2EnterGateway.Data;

public sealed class LocationRepository
{
    private readonly string _connectionString;

    public LocationRepository(IConfiguration configuration)
    {
        _connectionString =
            configuration.GetConnectionString("DueDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'DueDatabase' is missing.");
    }

    public async Task<List<LocationDto>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new List<LocationDto>();

        const string sql = """
            SELECT
                IdUbicazione,
                Ubicazione
            FROM dbo.tabUbicazioni
            WHERE IdUbicazione >= 0
            ORDER BY Ubicazione;
            """;

        await using var connection =
            new SqlConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);

        await using var command =
            new SqlCommand(sql, connection);

        command.CommandTimeout = 30;

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new LocationDto
            {
                Id = reader.IsDBNull(0)
                    ? 0
                    : reader.GetInt32(0),

                Name = reader.IsDBNull(1)
                    ? ""
                    : reader.GetString(1).Trim()
            });
        }

        return result;
    }

    public async Task<List<LocationDto>> GetByArticleAsync(
        int articleId,
        CancellationToken cancellationToken = default)
    {
        var result = new List<LocationDto>();

        const string sql = """
            SELECT
                u.IdUbicazione,
                u.Ubicazione
            FROM dbo.tabUbicazioniArticoli AS ua
            INNER JOIN dbo.tabUbicazioni AS u
                ON u.IdUbicazione = ua.IdUbicazione
            WHERE ua.IdArticolo = @articleId
            ORDER BY u.Ubicazione;
            """;

        await using var connection =
            new SqlConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);

        await using var command =
            new SqlCommand(sql, connection);

        command.CommandTimeout = 30;

        command.Parameters.Add(
            "@articleId",
            System.Data.SqlDbType.Int
        ).Value = articleId;

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new LocationDto
            {
                Id = reader.IsDBNull(0)
                    ? 0
                    : reader.GetInt32(0),

                Name = reader.IsDBNull(1)
                    ? ""
                    : reader.GetString(1).Trim()
            });
        }

        return result;
    }
    public async Task<bool> AddLocationAsync(
    int articleId,
    int locationId,
    CancellationToken cancellationToken = default)
    {
        await using var connection =
            new SqlConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);

        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            const string existsSql = """
            SELECT COUNT(1)
            FROM dbo.tabUbicazioniArticoli WITH (UPDLOCK, HOLDLOCK)
            WHERE IdArticolo = @articleId
              AND IdUbicazione = @locationId;
            """;

            await using var existsCommand =
                new SqlCommand(existsSql, connection, (SqlTransaction)transaction);

            existsCommand.Parameters.Add(
                "@articleId",
                System.Data.SqlDbType.Int
            ).Value = articleId;

            existsCommand.Parameters.Add(
                "@locationId",
                System.Data.SqlDbType.Int
            ).Value = locationId;

            var exists =
                Convert.ToInt32(
                    await existsCommand.ExecuteScalarAsync(cancellationToken)
                ) > 0;

            if (exists)
            {
                await transaction.CommitAsync(cancellationToken);
                return false;
            }

            const string insertSql = """
            DECLARE @newId int;

            SELECT @newId = ISNULL(MAX(ID), 0) + 1
            FROM dbo.tabUbicazioniArticoli WITH (UPDLOCK, HOLDLOCK);

            INSERT INTO dbo.tabUbicazioniArticoli
            (
                ID,
                IdUbicazione,
                IdArticolo,
                dataAgg,
                UtenteUltimoAccesso
            )
            VALUES
            (
                @newId,
                @locationId,
                @articleId,
                GETDATE(),
                'Scan2EnterGateway'
            );
            """;

            await using var insertCommand =
                new SqlCommand(insertSql, connection, (SqlTransaction)transaction);

            insertCommand.Parameters.Add(
                "@articleId",
                System.Data.SqlDbType.Int
            ).Value = articleId;

            insertCommand.Parameters.Add(
                "@locationId",
                System.Data.SqlDbType.Int
            ).Value = locationId;

            await insertCommand.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return true;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<bool> RemoveLocationAsync(
        int articleId,
        int locationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            new SqlConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);

        const string sql = """
            DELETE FROM dbo.tabUbicazioniArticoli
            WHERE IdArticolo = @articleId
              AND IdUbicazione = @locationId;
            """;

        await using var command =
            new SqlCommand(sql, connection);

        command.Parameters.Add(
            "@articleId",
            System.Data.SqlDbType.Int
        ).Value = articleId;

        command.Parameters.Add(
            "@locationId",
            System.Data.SqlDbType.Int
        ).Value = locationId;

        var rows = await command.ExecuteNonQueryAsync(cancellationToken);

        return rows > 0;
    }

}