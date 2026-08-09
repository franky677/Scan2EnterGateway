using Microsoft.Data.SqlClient;

namespace Scan2EnterGateway.Data;

public sealed class ClientPriceRepository
{
    private readonly string _connectionString;

    public ClientPriceRepository(IConfiguration configuration)
    {
        _connectionString =
            configuration.GetConnectionString("DueDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'DueDatabase' is missing.");
    }

    public async Task<ClientPriceResult?> GetAsync(
        int clientId,
        string barcode,
        CancellationToken cancellationToken = default)
    {
        if (clientId <= 0 || string.IsNullOrWhiteSpace(barcode))
        {
            return null;
        }

        const string sql = """
            SELECT TOP (1)
                c.IdCliente,
                LTRIM(RTRIM(
                    ISNULL(c.RagioneSociale1, '') + ' ' +
                    ISNULL(c.RagioneSociale2, '')
                )) AS Cliente,

                c.IdListino AS IdListinoCliente,
                c.Sconto1,
                c.Sconto2,
                c.Sconto3,
                c.Sconto4,

                a.IdArticolo,
                a.CodiceArticolo,
                a.Descrizione,
                b.Barcode,

                listino.IdListino,
                listino.NomeListino,

                pv.Imponibile,
                pv.PrezzoVendita

            FROM dbo.tabClienti AS c

            INNER JOIN dbo.tabBarcode AS b
                ON LTRIM(RTRIM(b.Barcode)) = @barcode

            INNER JOIN dbo.tabArticoli AS a
                ON a.IdArticolo = b.IdArticolo

            OUTER APPLY
            (
                SELECT TOP (1)
                    tl.IdListino,
                    tl.NomeListino
                FROM dbo.TabTipoListini AS tl
                WHERE
                    (
                        c.IdListino > 0
                        AND tl.IdListino = c.IdListino
                    )
                    OR
                    (
                        c.IdListino <= 0
                        AND tl.NomeListino = N'3-AL PUBBLICO'
                    )
                ORDER BY
                    CASE
                        WHEN tl.IdListino = c.IdListino THEN 0
                        ELSE 1
                    END
            ) AS listino

            LEFT JOIN dbo.tabPrezziVendita AS pv
                ON pv.IdArticolo = a.IdArticolo
               AND pv.IdListino = listino.IdListino
               AND ISNULL(pv.IdVariante1, -1) = -1
               AND ISNULL(pv.IdVariante2, -1) = -1
               AND ISNULL(pv.IdVariante3, -1) = -1

            WHERE c.IdCliente = @clientId;
            """;

        await using var connection =
            new SqlConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);

        await using var command =
            new SqlCommand(sql, connection);

        command.CommandTimeout = 15;

        command.Parameters.Add(
            "@clientId",
            System.Data.SqlDbType.Int).Value = clientId;

        command.Parameters.Add(
            "@barcode",
            System.Data.SqlDbType.NVarChar,
            100).Value = barcode.Trim();

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var listPrice = GetDecimal(reader, "PrezzoVendita");
        var discount1 = GetDecimal(reader, "Sconto1") ?? 0m;
        var discount2 = GetDecimal(reader, "Sconto2") ?? 0m;
        var discount3 = GetDecimal(reader, "Sconto3") ?? 0m;
        var discount4 = GetDecimal(reader, "Sconto4") ?? 0m;

        decimal? finalPrice = null;

        if (listPrice.HasValue)
        {
            // Regola verificata sul Comune di Mirano:
            // prezzo listino 5-MAX 2,86 - Sconto1 50% = 1,43.
            finalPrice = Math.Round(
                listPrice.Value * (1m - discount1 / 100m),
                2,
                MidpointRounding.AwayFromZero);
        }

        return new ClientPriceResult
        {
            ClientId = GetInt(reader, "IdCliente"),
            ClientName = GetString(reader, "Cliente"),

            ClientPriceListId =
                GetInt(reader, "IdListinoCliente"),

            PriceListId =
                GetInt(reader, "IdListino"),

            PriceListName =
                GetString(reader, "NomeListino"),

            ArticleId =
                GetInt(reader, "IdArticolo"),

            ArticleCode =
                GetString(reader, "CodiceArticolo"),

            Description =
                GetString(reader, "Descrizione"),

            Barcode =
                GetString(reader, "Barcode"),

            ListPrice = listPrice,

            Discount1 = discount1,
            Discount2 = discount2,
            Discount3 = discount3,
            Discount4 = discount4,

            FinalPrice = finalPrice
        };
    }

    private static string GetString(
        SqlDataReader reader,
        string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);

        return reader.IsDBNull(ordinal)
            ? ""
            : Convert.ToString(reader.GetValue(ordinal))?.Trim() ?? "";
    }

    private static int GetInt(
        SqlDataReader reader,
        string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);

        return reader.IsDBNull(ordinal)
            ? 0
            : Convert.ToInt32(reader.GetValue(ordinal));
    }

    private static decimal? GetDecimal(
        SqlDataReader reader,
        string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);

        return reader.IsDBNull(ordinal)
            ? null
            : Convert.ToDecimal(reader.GetValue(ordinal));
    }
}

public sealed class ClientPriceResult
{
    public int ClientId { get; init; }
    public string ClientName { get; init; } = "";

    public int ClientPriceListId { get; init; }

    public int PriceListId { get; init; }
    public string PriceListName { get; init; } = "";

    public int ArticleId { get; init; }
    public string ArticleCode { get; init; } = "";
    public string Description { get; init; } = "";
    public string Barcode { get; init; } = "";

    public decimal? ListPrice { get; init; }

    public decimal Discount1 { get; init; }
    public decimal Discount2 { get; init; }
    public decimal Discount3 { get; init; }
    public decimal Discount4 { get; init; }

    public decimal? FinalPrice { get; init; }
}