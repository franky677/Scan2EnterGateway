using System.Text.RegularExpressions;
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

    public async Task<LocationDto> CreateLocationAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = (name ?? "").Trim().ToUpperInvariant();

        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            throw new ArgumentException("Il nome dell'ubicazione è obbligatorio.", nameof(name));
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            const string existingSql = """
                SELECT TOP (1) IdUbicazione, Ubicazione
                FROM dbo.tabUbicazioni WITH (UPDLOCK, HOLDLOCK)
                WHERE UPPER(LTRIM(RTRIM(Ubicazione))) = @name;
                """;

            await using var existingCommand =
                new SqlCommand(existingSql, connection, (SqlTransaction)transaction);
            existingCommand.Parameters.Add("@name", System.Data.SqlDbType.NVarChar, 255)
                .Value = normalizedName;

            await using (var reader = await existingCommand.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    var existing = new LocationDto
                    {
                        Id = reader.GetInt32(0),
                        Name = reader.IsDBNull(1) ? normalizedName : reader.GetString(1).Trim()
                    };

                    await reader.DisposeAsync();
                    await transaction.CommitAsync(cancellationToken);
                    return existing;
                }
            }

            const string insertSql = """
                DECLARE @newId int;

                SELECT @newId = ISNULL(MAX(IdUbicazione), 0) + 1
                FROM dbo.tabUbicazioni WITH (UPDLOCK, HOLDLOCK);

                INSERT INTO dbo.tabUbicazioni
                (
                    IdUbicazione,
                    Ubicazione
                )
                VALUES
                (
                    @newId,
                    @name
                );

                SELECT @newId;
                """;

            await using var insertCommand =
                new SqlCommand(insertSql, connection, (SqlTransaction)transaction);
            insertCommand.Parameters.Add("@name", System.Data.SqlDbType.NVarChar, 255)
                .Value = normalizedName;

            var newId = Convert.ToInt32(
                await insertCommand.ExecuteScalarAsync(cancellationToken));

            await transaction.CommitAsync(cancellationToken);

            return new LocationDto
            {
                Id = newId,
                Name = normalizedName
            };
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<int> GetLocationUsageCountAsync(
        int locationId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT COUNT(DISTINCT IdArticolo)
            FROM dbo.tabUbicazioniArticoli
            WHERE IdUbicazione = @locationId;
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@locationId", System.Data.SqlDbType.Int).Value = locationId;

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<bool> DeleteLocationAsync(
        int locationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            const string usageSql = """
                SELECT COUNT(DISTINCT IdArticolo)
                FROM dbo.tabUbicazioniArticoli WITH (UPDLOCK, HOLDLOCK)
                WHERE IdUbicazione = @locationId;
                """;

            await using var usageCommand =
                new SqlCommand(usageSql, connection, (SqlTransaction)transaction);
            usageCommand.Parameters.Add("@locationId", System.Data.SqlDbType.Int).Value = locationId;

            var usageCount = Convert.ToInt32(
                await usageCommand.ExecuteScalarAsync(cancellationToken));

            if (usageCount > 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return false;
            }

            const string deleteSql = """
                DELETE FROM dbo.tabUbicazioni
                WHERE IdUbicazione = @locationId;
                """;

            await using var deleteCommand =
                new SqlCommand(deleteSql, connection, (SqlTransaction)transaction);
            deleteCommand.Parameters.Add("@locationId", System.Data.SqlDbType.Int).Value = locationId;

            var rows = await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return rows > 0;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }


    public async Task<LocationRenameResult> RenameLocationAsync(
        int locationId,
        string name,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = (name ?? "").Trim().ToUpperInvariant();

        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            throw new ArgumentException(
                "Il nome dell'ubicazione è obbligatorio.",
                nameof(name));
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            const string currentSql = """
                SELECT TOP (1) IdUbicazione, Ubicazione
                FROM dbo.tabUbicazioni WITH (UPDLOCK, HOLDLOCK)
                WHERE IdUbicazione = @locationId;
                """;

            await using var currentCommand =
                new SqlCommand(currentSql, connection, (SqlTransaction)transaction);
            currentCommand.Parameters.Add("@locationId", System.Data.SqlDbType.Int)
                .Value = locationId;

            LocationDto? current = null;
            await using (var reader =
                await currentCommand.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    current = new LocationDto
                    {
                        Id = reader.GetInt32(0),
                        Name = reader.IsDBNull(1) ? "" : reader.GetString(1).Trim()
                    };
                }
            }

            if (current is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return new LocationRenameResult(LocationRenameStatus.NotFound, null);
            }

            const string duplicateSql = """
                SELECT TOP (1) IdUbicazione, Ubicazione
                FROM dbo.tabUbicazioni WITH (UPDLOCK, HOLDLOCK)
                WHERE IdUbicazione <> @locationId
                  AND UPPER(LTRIM(RTRIM(Ubicazione))) = @name;
                """;

            await using var duplicateCommand =
                new SqlCommand(duplicateSql, connection, (SqlTransaction)transaction);
            duplicateCommand.Parameters.Add("@locationId", System.Data.SqlDbType.Int)
                .Value = locationId;
            duplicateCommand.Parameters.Add("@name", System.Data.SqlDbType.NVarChar, 255)
                .Value = normalizedName;

            LocationDto? duplicate = null;
            await using (var reader =
                await duplicateCommand.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    duplicate = new LocationDto
                    {
                        Id = reader.GetInt32(0),
                        Name = reader.IsDBNull(1)
                            ? normalizedName
                            : reader.GetString(1).Trim()
                    };
                }
            }

            if (duplicate is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return new LocationRenameResult(
                    LocationRenameStatus.Duplicate,
                    duplicate);
            }

            const string updateSql = """
                UPDATE dbo.tabUbicazioni
                SET Ubicazione = @name
                WHERE IdUbicazione = @locationId;
                """;

            await using var updateCommand =
                new SqlCommand(updateSql, connection, (SqlTransaction)transaction);
            updateCommand.Parameters.Add("@name", System.Data.SqlDbType.NVarChar, 255)
                .Value = normalizedName;
            updateCommand.Parameters.Add("@locationId", System.Data.SqlDbType.Int)
                .Value = locationId;

            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new LocationRenameResult(
                LocationRenameStatus.Renamed,
                new LocationDto { Id = locationId, Name = normalizedName });
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<LocationDto?> DuplicateNextLocationAsync(
        int locationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            const string currentSql = """
                SELECT TOP (1) Ubicazione
                FROM dbo.tabUbicazioni WITH (UPDLOCK, HOLDLOCK)
                WHERE IdUbicazione = @locationId;
                """;

            await using var currentCommand =
                new SqlCommand(currentSql, connection, (SqlTransaction)transaction);
            currentCommand.Parameters.Add("@locationId", System.Data.SqlDbType.Int)
                .Value = locationId;

            var currentName = Convert.ToString(
                await currentCommand.ExecuteScalarAsync(cancellationToken)
            )?.Trim().ToUpperInvariant();

            if (string.IsNullOrWhiteSpace(currentName))
            {
                await transaction.CommitAsync(cancellationToken);
                return null;
            }

            var match = Regex.Match(currentName, @"^(.*?)(\d+)\s*$");

            if (!match.Success)
            {
                throw new InvalidOperationException(
                    "Il nome deve terminare con un numero per creare la successiva.");
            }

            var prefix = match.Groups[1].Value;
            var numericText = match.Groups[2].Value;
            var width = numericText.Length;

            if (!long.TryParse(numericText, out var number))
            {
                throw new InvalidOperationException(
                    "Il numero finale dell'ubicazione non è valido.");
            }

            const string existsSql = """
                SELECT COUNT(1)
                FROM dbo.tabUbicazioni WITH (UPDLOCK, HOLDLOCK)
                WHERE UPPER(LTRIM(RTRIM(Ubicazione))) = @name;
                """;

            string nextName;

            while (true)
            {
                checked { number++; }
                nextName = prefix + number.ToString($"D{width}");

                await using var existsCommand =
                    new SqlCommand(existsSql, connection, (SqlTransaction)transaction);
                existsCommand.Parameters.Add("@name", System.Data.SqlDbType.NVarChar, 255)
                    .Value = nextName;

                var exists = Convert.ToInt32(
                    await existsCommand.ExecuteScalarAsync(cancellationToken)
                ) > 0;

                if (!exists) break;
            }

            const string insertSql = """
                DECLARE @newId int;

                SELECT @newId = ISNULL(MAX(IdUbicazione), 0) + 1
                FROM dbo.tabUbicazioni WITH (UPDLOCK, HOLDLOCK);

                INSERT INTO dbo.tabUbicazioni (IdUbicazione, Ubicazione)
                VALUES (@newId, @name);

                SELECT @newId;
                """;

            await using var insertCommand =
                new SqlCommand(insertSql, connection, (SqlTransaction)transaction);
            insertCommand.Parameters.Add("@name", System.Data.SqlDbType.NVarChar, 255)
                .Value = nextName;

            var newId = Convert.ToInt32(
                await insertCommand.ExecuteScalarAsync(cancellationToken));

            await transaction.CommitAsync(cancellationToken);

            return new LocationDto { Id = newId, Name = nextName };
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

}


public enum LocationRenameStatus
{
    Renamed,
    NotFound,
    Duplicate
}

public sealed record LocationRenameResult(
    LocationRenameStatus Status,
    LocationDto? Location);
