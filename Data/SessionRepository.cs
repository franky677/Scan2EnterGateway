using Microsoft.Data.SqlClient;
using Scan2EnterGateway.Models;

namespace Scan2EnterGateway.Data;

public sealed class SessionRepository
{
    private readonly string _connectionString;

    public SessionRepository(IConfiguration configuration)
    {
        _connectionString =
            configuration.GetConnectionString("DueDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'DueDatabase' is missing.");
    }

    public async Task<List<SessionHistoryItemDto>> SearchHistoryAsync(
        int clientId,
        string query,
        CancellationToken cancellationToken = default)
    {
        var result = new List<SessionHistoryItemDto>();
        var normalizedQuery = (query ?? "").Trim();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        // Se la query è un codice articolo (es. VIW14000),
        // recuperiamo il primo barcode associato.
        if (!normalizedQuery.All(char.IsDigit))
        {
            const string barcodeSql = """
                SELECT TOP (1) b.Barcode
                FROM dbo.tabArticoli a
                INNER JOIN dbo.tabBarcode b
                    ON b.idArticolo = a.idArticolo
                WHERE a.CodiceArticolo = @articleCode
                ORDER BY b.Barcode;
                """;

            await using var barcodeCmd = new SqlCommand(barcodeSql, connection);
            barcodeCmd.Parameters.AddWithValue("@articleCode", normalizedQuery);

            var barcode = await barcodeCmd.ExecuteScalarAsync(cancellationToken);

            if (barcode is string s && !string.IsNullOrWhiteSpace(s))
            {
                normalizedQuery = s.Trim();
            }
        }


        if (clientId <= 0 || string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return result;
        }

        const string sql = """
            SELECT TOP (100)
                tc.idTestata,
                tc.NumeroCollo,
                tc.IdCliente,
                tc.DataCreaz,
                tc.dataAgg,

                LTRIM(RTRIM(
                    ISNULL(c.RagioneSociale1, '') + ' ' +
                    ISNULL(c.RagioneSociale2, '')
                )) AS Cliente,

                dc.idDettaglio,
                dc.BarCode AS Barcode,
                dc.Descrizione,
                dc.Prezzo,
                dc.Quantita,
                dc.TotaleNettoSconto

            FROM dbo.tabTestateColli AS tc
            INNER JOIN dbo.tabDettaglioColli AS dc
                ON dc.IdTestata = tc.idTestata
            LEFT JOIN dbo.tabClienti AS c
                ON c.IdCliente = tc.IdCliente

            WHERE tc.IdCliente = @clientId
              AND (
                    dc.BarCode = @query
                    OR dc.Descrizione LIKE '%' + @query + '%'
                  )

            ORDER BY tc.idTestata DESC;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = 15;

        command.Parameters.Add("@clientId",
            System.Data.SqlDbType.Int).Value = clientId;

        command.Parameters.Add("@query",
            System.Data.SqlDbType.NVarChar,
            255).Value = normalizedQuery;

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new SessionHistoryItemDto
            {
                TestataId = GetInt(reader, "idTestata"),
                DetailId = GetInt(reader, "idDettaglio"),
                NumeroCollo = GetString(reader, "NumeroCollo"),

                ClientId = GetInt(reader, "IdCliente"),
                ClientName = GetString(reader, "Cliente"),

                ArticleId = 0,
                ArticleCode = "",
                Barcode = GetString(reader, "Barcode"),
                Description = GetString(reader, "Descrizione"),

                Price = GetDecimal(reader, "Prezzo"),
                Quantity = GetDecimal(reader, "Quantita"),
                Total = GetDecimal(reader, "TotaleNettoSconto"),

                Date = GetDate(reader, "dataAgg", "DataCreaz")
            });
        }

        return result;
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

    private static DateTime? GetDate(
        SqlDataReader reader,
        string preferredColumn,
        string fallbackColumn)
    {
        var preferred = reader.GetOrdinal(preferredColumn);

        if (!reader.IsDBNull(preferred))
        {
            return Convert.ToDateTime(reader.GetValue(preferred));
        }

        var fallback = reader.GetOrdinal(fallbackColumn);

        return reader.IsDBNull(fallback)
            ? null
            : Convert.ToDateTime(reader.GetValue(fallback));
    }
}