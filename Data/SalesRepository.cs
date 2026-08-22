using Microsoft.Data.SqlClient;
using Scan2EnterGateway.Models;

namespace Scan2EnterGateway.Data;

public sealed class SalesRepository
{
    private readonly string _connectionString;

    public SalesRepository(IConfiguration configuration)
    {
        _connectionString =
            configuration.GetConnectionString("DueDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'DueDatabase' is missing.");
    }

    public async Task<SalesSummaryDto> GetSummaryAsync(
        int year,
        CancellationToken ct)
    {
        var from = new DateTime(year, 1, 1);
        var toExclusive = from.AddYears(1);

        const string sql = """
WITH Corrispettivi AS
(
    SELECT
        COUNT(DISTINCT t.idTestata) AS NumeroDocumenti,
        SUM(CAST(d.Imponibile AS decimal(18, 4))) AS VenditeImponibili,
        SUM(CAST(d.CostoTotaleImponibile AS decimal(18, 4))) AS CostoVenduto
    FROM dbo.tabTestateScontrini AS t
    INNER JOIN dbo.tabDettaglioScontrini AS d
        ON d.idTestata = t.idTestata
    WHERE
        t.IdCausale = 4
        AND t.DataOraScontrino >= @from
        AND t.DataOraScontrino < @toExclusive
        AND d.IdArticolo IS NOT NULL
        AND d.IdArticolo > 0
),
Fatture AS
(
    SELECT
        COUNT(DISTINCT f.IdTestata) AS NumeroDocumenti,
        SUM(CAST(d.Imponibile AS decimal(18, 4))) AS VenditeImponibili,
        SUM(CAST(d.CostoTotaleImponibile AS decimal(18, 4))) AS CostoVenduto
    FROM dbo.tabTestateFatture AS f
    INNER JOIN dbo.tabDettaglioFatture AS d
        ON d.IdTestata = f.IdTestata
    WHERE
        f.IdCausale = 27
        AND f.DataDocumento >= @from
        AND f.DataDocumento < @toExclusive
        AND d.IdArticolo IS NOT NULL
        AND d.IdArticolo > 0
)
SELECT
    ISNULL(c.NumeroDocumenti, 0) AS ReceiptsDocuments,
    ISNULL(c.VenditeImponibili, 0) AS ReceiptsSalesTaxable,
    ISNULL(c.CostoVenduto, 0) AS ReceiptsCost,

    ISNULL(f.NumeroDocumenti, 0) AS InvoicesDocuments,
    ISNULL(f.VenditeImponibili, 0) AS InvoicesSalesTaxable,
    ISNULL(f.CostoVenduto, 0) AS InvoicesCost
FROM Corrispettivi AS c
CROSS JOIN Fatture AS f;
""";

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = 60;
        command.Parameters.AddWithValue("@from", from);
        command.Parameters.AddWithValue("@toExclusive", toExclusive);

        await using var reader = await command.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
        {
            return new SalesSummaryDto
            {
                Year = year,
                From = from,
                To = DateTime.Today
            };
        }

        var receipts = BuildSection(
            Integer(reader, "ReceiptsDocuments"),
            Number(reader, "ReceiptsSalesTaxable"),
            Number(reader, "ReceiptsCost"));

        var invoices = BuildSection(
            Integer(reader, "InvoicesDocuments"),
            Number(reader, "InvoicesSalesTaxable"),
            Number(reader, "InvoicesCost"));

        var total = BuildSection(
            receipts.Documents + invoices.Documents,
            receipts.SalesTaxable + invoices.SalesTaxable,
            receipts.Cost + invoices.Cost);

        var requestedEnd = toExclusive.AddDays(-1);
        var actualTo = year == DateTime.Today.Year
            ? DateTime.Today
            : requestedEnd;

        return new SalesSummaryDto
        {
            Year = year,
            From = from,
            To = actualTo,
            Receipts = receipts,
            Invoices = invoices,
            Total = total
        };
    }

    private static SalesSummarySectionDto BuildSection(
        int documents,
        decimal salesTaxable,
        decimal cost)
    {
        var difference = salesTaxable - cost;

        var markup = cost == 0m
            ? 0m
            : difference / cost * 100m;

        return new SalesSummarySectionDto
        {
            Documents = documents,
            SalesTaxable = Math.Round(salesTaxable, 2),
            Cost = Math.Round(cost, 2),
            Difference = Math.Round(difference, 2),
            MarkupPercent = Math.Round(markup, 2)
        };
    }

    private static int Integer(
        SqlDataReader reader,
        string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);

        return reader.IsDBNull(ordinal)
            ? 0
            : Convert.ToInt32(reader.GetValue(ordinal));
    }

    private static decimal Number(
        SqlDataReader reader,
        string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);

        return reader.IsDBNull(ordinal)
            ? 0m
            : Convert.ToDecimal(reader.GetValue(ordinal));
    }
}
