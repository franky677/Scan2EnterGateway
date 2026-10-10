using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace Scan2EnterGateway.Data;

public sealed class DeliveryReportEmailRepository
{
    private const string DataDirectory = @"C:\Scan2EnterData";

    private static readonly string DatabasePath =
        Path.Combine(DataDirectory, "Scan2Enter.db");

    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;

    public DeliveryReportEmailRepository(IConfiguration configuration)
    {
        _connectionString =
            configuration.GetConnectionString("DueDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'DueDatabase' is missing.");
    }

    public async Task<bool> IsEnabledForClientAsync(
        int clientId,
        CancellationToken cancellationToken = default)
    {
        if (clientId <= 0)
        {
            return false;
        }

        await EnsureInitializedAsync(cancellationToken);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Enabled
            FROM DeliveryReportEmailClients
            WHERE ClientId = $clientId
            LIMIT 1;
            """;

        command.Parameters.AddWithValue(
            "$clientId",
            clientId);

        var value =
            await command.ExecuteScalarAsync(
                cancellationToken);

        return value is not null &&
               Convert.ToInt32(value) != 0;
    }

    public async Task SetEnabledForClientAsync(
        int clientId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        if (clientId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(clientId));
        }

        await EnsureInitializedAsync(cancellationToken);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO DeliveryReportEmailClients
            (
                ClientId,
                Enabled,
                UpdatedAtUtc
            )
            VALUES
            (
                $clientId,
                $enabled,
                $updatedAtUtc
            )
            ON CONFLICT(ClientId) DO UPDATE SET
                Enabled = excluded.Enabled,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;

        command.Parameters.AddWithValue(
            "$clientId",
            clientId);

        command.Parameters.AddWithValue(
            "$enabled",
            enabled ? 1 : 0);

        command.Parameters.AddWithValue(
            "$updatedAtUtc",
            DateTimeOffset.UtcNow.ToString("O"));

        await command.ExecuteNonQueryAsync(
            cancellationToken);
    }

    public async Task<string?> GetRecipientEmailAsync(
        int clientId,
        CancellationToken cancellationToken = default)
    {
        if (clientId <= 0)
        {
            return null;
        }

        const string sql = """
            SELECT TOP (1)
                LTRIM(RTRIM(ISNULL(Email, ''))) AS Email
            FROM dbo.tabClienti
            WHERE IdCliente = @clientId;
            """;

        await using var connection =
            new SqlConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);

        await using var command =
            new SqlCommand(sql, connection);

        command.Parameters.AddWithValue(
            "@clientId",
            clientId);

        var value =
            await command.ExecuteScalarAsync(
                cancellationToken);

        var email =
            value?.ToString()?.Trim();

        return string.IsNullOrWhiteSpace(email)
            ? null
            : email;
    }

    public async Task<bool> WasSentAsync(
        int testataId,
        CancellationToken cancellationToken = default)
    {
        if (testataId <= 0)
        {
            return false;
        }

        await EnsureInitializedAsync(cancellationToken);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 1
            FROM DeliveryReportEmailLog
            WHERE TestataId = $testataId
            LIMIT 1;
            """;

        command.Parameters.AddWithValue(
            "$testataId",
            testataId);

        var value =
            await command.ExecuteScalarAsync(
                cancellationToken);

        return value is not null;
    }

    public async Task MarkSentAsync(
        int testataId,
        int clientId,
        string numeroCollo,
        string recipientEmail,
        CancellationToken cancellationToken = default)
    {
        if (testataId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(testataId));
        }

        await EnsureInitializedAsync(cancellationToken);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR IGNORE INTO DeliveryReportEmailLog
            (
                TestataId,
                ClientId,
                NumeroCollo,
                RecipientEmail,
                SentAtUtc
            )
            VALUES
            (
                $testataId,
                $clientId,
                $numeroCollo,
                $recipientEmail,
                $sentAtUtc
            );
            """;

        command.Parameters.AddWithValue(
            "$testataId",
            testataId);

        command.Parameters.AddWithValue(
            "$clientId",
            clientId);

        command.Parameters.AddWithValue(
            "$numeroCollo",
            numeroCollo?.Trim() ?? "");

        command.Parameters.AddWithValue(
            "$recipientEmail",
            recipientEmail?.Trim() ?? "");

        command.Parameters.AddWithValue(
            "$sentAtUtc",
            DateTimeOffset.UtcNow.ToString("O"));

        await command.ExecuteNonQueryAsync(
            cancellationToken);
    }

    private static SqliteConnection CreateConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };

        return new SqliteConnection(
            builder.ToString());
    }

    private async Task EnsureInitializedAsync(
        CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializeLock.WaitAsync(
            cancellationToken);

        try
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(
                DataDirectory);

            await using var connection =
                CreateConnection();

            await connection.OpenAsync(
                cancellationToken);

            await using var command =
                connection.CreateCommand();

            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS DeliveryReportEmailLog
                (
                    TestataId INTEGER NOT NULL PRIMARY KEY,
                    ClientId INTEGER NOT NULL,
                    NumeroCollo TEXT NOT NULL DEFAULT '',
                    RecipientEmail TEXT NOT NULL DEFAULT '',
                    SentAtUtc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS DeliveryReportEmailClients
                (
                    ClientId INTEGER NOT NULL PRIMARY KEY,
                    Enabled INTEGER NOT NULL DEFAULT 0,
                    UpdatedAtUtc TEXT NOT NULL
                );

                INSERT OR IGNORE INTO DeliveryReportEmailClients
                (
                    ClientId,
                    Enabled,
                    UpdatedAtUtc
                )
                VALUES
                (
                    216,
                    1,
                    'migration-pilot'
                );
                """;

            await command.ExecuteNonQueryAsync(
                cancellationToken);

            _initialized = true;
        }
        finally
        {
            _initializeLock.Release();
        }
    }
}
