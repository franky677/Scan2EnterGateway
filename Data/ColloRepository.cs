using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Scan2EnterGateway.Models;

namespace Scan2EnterGateway.Data;

public sealed class ColloRepository
{
    private readonly string _connectionString;

    public ColloRepository(IConfiguration configuration)
    {
        _connectionString =
            configuration.GetConnectionString("DueDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'DueDatabase' is missing.");
    }

    public async Task<CreatedColloDto> CreateAsync(
        CreateColloRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var transaction =
            (SqlTransaction)await connection.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);

        try
        {
            var now = DateTime.Now;

            var client = await ReadClientAsync(
                connection,
                transaction,
                request.ClientId,
                cancellationToken);

            if (client is null)
            {
                throw new InvalidOperationException(
                    $"Cliente {request.ClientId} non trovato.");
            }

            var newTestataId = await NextIdAsync(
                connection,
                transaction,
                "dbo.tabTestateColli",
                "idTestata",
                cancellationToken);

            var nextDetailId = await NextIdAsync(
                connection,
                transaction,
                "dbo.tabDettaglioColli",
                "idDettaglio",
                cancellationToken);

            var numeroCollo = await NextClassicColloNumberAsync(
                connection,
                transaction,
                cancellationToken);

            var preparedItems = new List<PreparedItem>();

            foreach (var item in request.Items)
            {
                var article = await ReadArticleAsync(
                    connection,
                    transaction,
                    item.Barcode.Trim(),
                    cancellationToken);

                if (article is null)
                {
                    throw new InvalidOperationException(
                        $"Barcode '{item.Barcode}' non trovato.");
                }

                var vatRate = article.Value.VatRate;

                var grossUnitPrice =
                    item.ListPrice.HasValue && item.ListPrice.Value >= 0m
                        ? item.ListPrice.Value
                        : item.Price;

                var discounts = NormalizeDiscounts(
                    item.Discount1,
                    item.Discount2,
                    item.Discount3,
                    item.Discount4,
                    item.ManualDiscount);

                var grossTotal = decimal.Round(
                    grossUnitPrice * item.Quantity,
                    2,
                    MidpointRounding.AwayFromZero);

                var netTotal = ApplyDiscounts(
                    grossTotal,
                    discounts);

                var discountAmount = decimal.Round(
                    grossTotal - netTotal,
                    2,
                    MidpointRounding.AwayFromZero);

                var divisor = 1m + (vatRate / 100m);

                var taxableTotal = divisor == 0m
                    ? netTotal
                    : decimal.Round(
                        netTotal / divisor,
                        2,
                        MidpointRounding.AwayFromZero);

                var vatTotal = decimal.Round(
                    netTotal - taxableTotal,
                    2,
                    MidpointRounding.AwayFromZero);

                var netUnit = divisor == 0m
                    ? grossUnitPrice
                    : decimal.Round(
                        grossUnitPrice / divisor,
                        4,
                        MidpointRounding.AwayFromZero);

                preparedItems.Add(new PreparedItem(
                    ArticleId: article.Value.ArticleId,
                    Barcode: item.Barcode.Trim(),
                    Description: article.Value.Description,
                    PriceListId: item.PriceListId ?? -1,
                    Price: grossUnitPrice,
                    Quantity: item.Quantity,
                    VatRate: vatRate,
                    Total: grossTotal,
                    Discount1: discounts[0],
                    Discount2: discounts[1],
                    Discount3: discounts[2],
                    Discount4: discounts[3],
                    DiscountAmount: discountAmount,
                    NetTotal: netTotal,
                    Taxable: taxableTotal,
                    Vat: vatTotal,
                    NetUnit: netUnit));
            }

            await InsertHeaderAsync(
                connection,
                transaction,
                newTestataId,
                numeroCollo,
                request.ClientId,
                client.Value.PaymentId,
                request.Note,
                now,
                cancellationToken);

            var detailId = nextDetailId;

            foreach (var item in preparedItems)
            {
                await InsertDetailAsync(
                    connection,
                    transaction,
                    detailId++,
                    newTestataId,
                    item,
                    now,
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);

            return new CreatedColloDto
            {
                TestataId = newTestataId,
                NumeroCollo = numeroCollo.ToString(
                    CultureInfo.InvariantCulture),
                ClientId = request.ClientId,
                ClientName = client.Value.Name,
                BarcodeCollo = BuildColloEan13(numeroCollo),
                ItemCount = preparedItems.Count,
                Total = preparedItems.Sum(x => x.NetTotal),
                CreatedAt = now
            };
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<List<ColloHistorySummaryDto>> SearchHistoryAsync(
        string? query,
        int days = 30,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        days = Math.Max(days, 0);
        var normalizedQuery = (query ?? string.Empty).Trim();

        /*
         * Strategia a due fasi.
         *
         * 1) Cerchiamo prima SOLO nelle testate:
         *    numero collo, cliente, annotazioni.
         *
         * 2) Soltanto se non abbiamo ancora raggiunto @limit,
         *    cerchiamo anche dentro righe/barcode/articoli.
         *
         * In questo modo una ricerca come "luciano" che corrisponde al nome
         * cliente non costringe SQL Server a scandire tabDettaglioColli.
         */
        const string sql = """
            CREATE TABLE #Candidates
            (
                idTestata INT NOT NULL PRIMARY KEY,
                SortDate DATETIME NULL
            );

            INSERT INTO #Candidates (idTestata, SortDate)
            SELECT TOP (@limit)
                t.idTestata,
                ISNULL(t.dataAgg, t.DataCreaz)
            FROM dbo.tabTestateColli AS t
            LEFT JOIN dbo.tabClienti AS c
                ON c.IdCliente = t.IdCliente
            WHERE t.TipoDocumentoPrenotato = 25
              AND (
                    @days = 0
                    OR t.DataCreaz >= DATEADD(DAY, -@days, CAST(GETDATE() AS date))
              )
              AND (
                    @query = ''
                    OR LTRIM(RTRIM(ISNULL(t.NumeroCollo, ''))) LIKE '%' + @query + '%'
                    OR ISNULL(c.RagioneSociale1, '') LIKE '%' + @query + '%'
                    OR ISNULL(c.RagioneSociale2, '') LIKE '%' + @query + '%'
                    OR ISNULL(t.Annotazioni, '') LIKE '%' + @query + '%'
              )
            ORDER BY
                ISNULL(t.dataAgg, t.DataCreaz) DESC,
                t.idTestata DESC;

            IF @query <> ''
               AND NOT EXISTS (SELECT 1 FROM #Candidates)
            BEGIN
                /*
                 * Fallback articoli: niente OR tra campi diversi.
                 * I tre rami sono separati e uniti con UNION, perché i test
                 * diretti su SQL Server risultano veloci singolarmente.
                 */

                INSERT INTO #Candidates (idTestata, SortDate)
                SELECT TOP (@limit)
                    q.IdTestata,
                    MAX(q.SortDate) AS SortDate
                FROM
                (
                    /* Descrizione riga */
                    SELECT
                        dx.IdTestata,
                        ISNULL(tx.dataAgg, tx.DataCreaz) AS SortDate
                    FROM dbo.tabDettaglioColli AS dx
                    INNER JOIN dbo.tabTestateColli AS tx
                        ON tx.idTestata = dx.IdTestata
                    WHERE tx.TipoDocumentoPrenotato = 25
                      AND (
                            @days = 0
                            OR tx.DataCreaz >= DATEADD(
                                DAY,
                                -@days,
                                CAST(GETDATE() AS date)
                            )
                      )
                      AND ISNULL(dx.Descrizione, '') LIKE '%' + @query + '%'

                    UNION

                    /* Barcode riga */
                    SELECT
                        dx.IdTestata,
                        ISNULL(tx.dataAgg, tx.DataCreaz) AS SortDate
                    FROM dbo.tabDettaglioColli AS dx
                    INNER JOIN dbo.tabTestateColli AS tx
                        ON tx.idTestata = dx.IdTestata
                    WHERE tx.TipoDocumentoPrenotato = 25
                      AND (
                            @days = 0
                            OR tx.DataCreaz >= DATEADD(
                                DAY,
                                -@days,
                                CAST(GETDATE() AS date)
                            )
                      )
                      AND ISNULL(dx.BarCode, '') LIKE '%' + @query + '%'

                    UNION

                    /* Codice articolo */
                    SELECT
                        dx.IdTestata,
                        ISNULL(tx.dataAgg, tx.DataCreaz) AS SortDate
                    FROM dbo.tabDettaglioColli AS dx
                    INNER JOIN dbo.tabTestateColli AS tx
                        ON tx.idTestata = dx.IdTestata
                    INNER JOIN dbo.tabBarcode AS bx
                        ON bx.Barcode = dx.BarCode
                    INNER JOIN dbo.tabArticoli AS ax
                        ON ax.idArticolo = bx.idArticolo
                    WHERE tx.TipoDocumentoPrenotato = 25
                      AND (
                            @days = 0
                            OR tx.DataCreaz >= DATEADD(
                                DAY,
                                -@days,
                                CAST(GETDATE() AS date)
                            )
                      )
                      AND ISNULL(ax.CodiceArticolo, '') LIKE '%' + @query + '%'
                ) AS q
                GROUP BY q.IdTestata
                ORDER BY
                    MAX(q.SortDate) DESC,
                    q.IdTestata DESC;
            END;

            SELECT
                t.idTestata,
                LTRIM(RTRIM(ISNULL(t.NumeroCollo, ''))) AS NumeroCollo,
                ISNULL(t.IdCliente, 0) AS IdCliente,
                LTRIM(RTRIM(
                    ISNULL(c.RagioneSociale1, '') + ' ' +
                    ISNULL(c.RagioneSociale2, '')
                )) AS Cliente,
                ISNULL(t.dataAgg, t.DataCreaz) AS CreatedAt,
                ISNULL(t.IsElaborato, 0) AS IsElaborato,
                CASE
                    WHEN NULLIF(
                        LTRIM(RTRIM(ISNULL(t.Annotazioni, ''))),
                        ''
                    ) IS NULL
                        THEN CAST(0 AS bit)
                    ELSE CAST(1 AS bit)
                END AS HasNote,
                ISNULL(a.ItemCount, 0) AS ItemCount,
                ISNULL(a.PieceCount, 0) AS PieceCount,
                ISNULL(a.Totale, 0) AS Totale
            FROM #Candidates AS h
            INNER JOIN dbo.tabTestateColli AS t
                ON t.idTestata = h.idTestata
            LEFT JOIN dbo.tabClienti AS c
                ON c.IdCliente = t.IdCliente
            OUTER APPLY
            (
                SELECT
                    COUNT(d.idDettaglio) AS ItemCount,
                    ISNULL(
                        SUM(ISNULL(d.Quantita, 0)),
                        0
                    ) AS PieceCount,
                    ISNULL(
                        SUM(
                            CASE
                                WHEN ISNULL(d.TotaleNettoSconto, 0) <> 0
                                    THEN d.TotaleNettoSconto
                                ELSE ISNULL(d.Totale, 0)
                            END
                        ),
                        0
                    ) AS Totale
                FROM dbo.tabDettaglioColli AS d
                WHERE d.IdTestata = h.idTestata
            ) AS a
            ORDER BY
                h.SortDate DESC,
                h.idTestata DESC;

            DROP TABLE #Candidates;
            """;

        var results = new List<ColloHistorySummaryDto>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);

        /*
         * Manteniamo 30 secondi: se questa versione va ancora in timeout
         * significa che il collo di bottiglia è nel ramo dettaglio, non
         * nella ricerca cliente/testata.
         */
        command.CommandTimeout = 30;

        command.Parameters.Add("@limit", SqlDbType.Int).Value = limit;
        command.Parameters.Add("@days", SqlDbType.Int).Value = days;
        command.Parameters.Add("@query", SqlDbType.NVarChar, 200).Value =
            normalizedQuery;

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var numeroCollo =
                reader["NumeroCollo"]?.ToString()?.Trim() ?? "";

            var numeroInt =
                int.TryParse(
                    numeroCollo,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var parsedNumero)
                    ? parsedNumero
                    : 0;

            results.Add(new ColloHistorySummaryDto
            {
                TestataId =
                    Convert.ToInt32(reader["idTestata"]),

                NumeroCollo =
                    numeroCollo,

                BarcodeCollo =
                    numeroInt > 0
                        ? BuildColloEan13(numeroInt)
                        : "",

                ClientId =
                    Convert.ToInt32(reader["IdCliente"]),

                ClientName =
                    reader["Cliente"]?.ToString()?.Trim() ?? "",

                CreatedAt =
                    reader["CreatedAt"] is DateTime createdAt
                        ? createdAt
                        : DateTime.MinValue,

                ItemCount =
                    Convert.ToInt32(reader["ItemCount"]),

                PieceCount =
                    Convert.ToDecimal(
                        reader["PieceCount"],
                        CultureInfo.InvariantCulture),

                Total =
                    Convert.ToDecimal(
                        reader["Totale"],
                        CultureInfo.InvariantCulture),

                IsElaborato =
                    Convert.ToBoolean(reader["IsElaborato"]),

                HasNote =
                    Convert.ToBoolean(reader["HasNote"])
            });
        }

        return results;
    }


    public async Task<ColloHistoryDetailDto?> GetHistoryDetailAsync(
        int testataId,
        CancellationToken cancellationToken = default)
    {
        const string headerSql = """
            SELECT TOP (1)
                t.idTestata,
                LTRIM(RTRIM(ISNULL(t.NumeroCollo, ''))) AS NumeroCollo,
                ISNULL(t.IdCliente, 0) AS IdCliente,
                LTRIM(RTRIM(ISNULL(c.RagioneSociale1, '') + ' ' + ISNULL(c.RagioneSociale2, ''))) AS Cliente,
                ISNULL(t.dataAgg, t.DataCreaz) AS CreatedAt,
                ISNULL(t.IsElaborato, 0) AS IsElaborato,
                LTRIM(RTRIM(ISNULL(t.Annotazioni, ''))) AS Annotazioni
            FROM dbo.tabTestateColli AS t
            LEFT JOIN dbo.tabClienti AS c ON c.IdCliente = t.IdCliente
            WHERE t.idTestata = @testataId AND t.TipoDocumentoPrenotato = 25;
            """;

        const string detailSql = """
            SELECT
                ISNULL(a.idArticolo, 0) AS idArticolo,
                ISNULL(a.CodiceArticolo, '') AS CodiceArticolo,
                LTRIM(RTRIM(ISNULL(d.Descrizione, ''))) AS Descrizione,
                LTRIM(RTRIM(ISNULL(d.BarCode, ''))) AS Barcode,
                ISNULL(d.Quantita, 0) AS Quantita,
                ISNULL(d.Prezzo, 0) AS Prezzo,
                CASE WHEN ISNULL(d.TotaleNettoSconto, 0) <> 0 THEN d.TotaleNettoSconto ELSE ISNULL(d.Totale, 0) END AS Totale
            FROM dbo.tabDettaglioColli AS d
            LEFT JOIN dbo.tabBarcode AS b ON LTRIM(RTRIM(b.Barcode)) = LTRIM(RTRIM(d.BarCode))
            LEFT JOIN dbo.tabArticoli AS a ON a.idArticolo = b.idArticolo
            WHERE d.IdTestata = @testataId
            ORDER BY d.idDettaglio;
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        ColloHistoryDetailDto? result;
        await using (var header = new SqlCommand(headerSql, connection))
        {
            header.Parameters.Add("@testataId", SqlDbType.Int).Value = testataId;
            await using var reader = await header.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            var numeroCollo = reader["NumeroCollo"]?.ToString()?.Trim() ?? "";
            var numeroInt = int.TryParse(numeroCollo, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedNumero) ? parsedNumero : 0;
            result = new ColloHistoryDetailDto
            {
                TestataId = Convert.ToInt32(reader["idTestata"]),
                NumeroCollo = numeroCollo,
                BarcodeCollo = numeroInt > 0 ? BuildColloEan13(numeroInt) : "",
                ClientId = Convert.ToInt32(reader["IdCliente"]),
                ClientName = reader["Cliente"]?.ToString()?.Trim() ?? "",
                CreatedAt = reader["CreatedAt"] is DateTime createdAt ? createdAt : DateTime.MinValue,
                IsElaborato = Convert.ToBoolean(reader["IsElaborato"]),
                Note = reader["Annotazioni"]?.ToString()?.Trim() ?? ""
            };
        }
        await using (var details = new SqlCommand(detailSql, connection))
        {
            details.Parameters.Add("@testataId", SqlDbType.Int).Value = testataId;
            await using var reader = await details.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Items.Add(new ColloHistoryItemDto
                {
                    ArticleId = Convert.ToInt64(reader["idArticolo"]),
                    ArticleCode = reader["CodiceArticolo"]?.ToString()?.Trim() ?? "",
                    Description = reader["Descrizione"]?.ToString()?.Trim() ?? "",
                    Barcode = reader["Barcode"]?.ToString()?.Trim() ?? "",
                    Quantity = Convert.ToDecimal(reader["Quantita"], CultureInfo.InvariantCulture),
                    Price = Convert.ToDecimal(reader["Prezzo"], CultureInfo.InvariantCulture),
                    Total = Convert.ToDecimal(reader["Totale"], CultureInfo.InvariantCulture)
                });
            }
        }
        result.Total = result.Items.Sum(x => x.Total);
        return result;
    }

    private static async Task<(string Name, int PaymentId)?> ReadClientAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int clientId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1)
                LTRIM(RTRIM(
                    ISNULL(RagioneSociale1, '') + ' ' +
                    ISNULL(RagioneSociale2, '')
                )) AS Cliente,
                ISNULL(IdPagamento, -1) AS IdPagamento
            FROM dbo.tabClienti
            WHERE IdCliente = @clientId;
            """;

        await using var command =
            new SqlCommand(sql, connection, transaction);

        command.Parameters.Add(
            "@clientId",
            SqlDbType.Int).Value = clientId;

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (
            reader.IsDBNull(0) ? "" : reader.GetString(0).Trim(),
            reader.IsDBNull(1) ? -1 : Convert.ToInt32(reader.GetValue(1))
        );
    }

    private static async Task<(long ArticleId, string Description, decimal VatRate)?>
        ReadArticleAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            string barcode,
            CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1)
                a.idArticolo,
                a.Descrizione,
                ISNULL(a.AliquotaIva, 0) AS AliquotaIva
            FROM dbo.tabBarcode AS b
            INNER JOIN dbo.tabArticoli AS a
                ON a.idArticolo = b.idArticolo
            WHERE b.Barcode = @barcode;
            """;

        await using var command =
            new SqlCommand(sql, connection, transaction);

        command.Parameters.Add(
            "@barcode",
            SqlDbType.NVarChar,
            60).Value = barcode;

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (
            reader.IsDBNull(0)
                ? 0L
                : Convert.ToInt64(
                    reader.GetValue(0),
                    CultureInfo.InvariantCulture),
            reader.IsDBNull(1) ? "" : reader.GetString(1).Trim(),
            reader.IsDBNull(2)
                ? 0m
                : Convert.ToDecimal(
                    reader.GetValue(2),
                    CultureInfo.InvariantCulture)
        );
    }

    private static async Task<int> NextIdAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT ISNULL(MAX({columnName}), 0) + 1
            FROM {tableName} WITH (UPDLOCK, HOLDLOCK);
            """;

        await using var command =
            new SqlCommand(sql, connection, transaction);

        var value = await command.ExecuteScalarAsync(cancellationToken);

        return Convert.ToInt32(
            value,
            CultureInfo.InvariantCulture);
    }

    private static async Task<int> NextClassicColloNumberAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT ISNULL(MAX(CAST(NumeroCollo AS INT)), 0) + 1
            FROM dbo.tabTestateColli WITH (UPDLOCK, HOLDLOCK)
            WHERE TipoDocumentoPrenotato = 25
              AND NumeroCollo IS NOT NULL
              AND NumeroCollo <> ''
              AND NumeroCollo NOT LIKE '%[^0-9]%';
            """;

        await using var command =
            new SqlCommand(sql, connection, transaction);

        var value = await command.ExecuteScalarAsync(cancellationToken);

        return Convert.ToInt32(
            value,
            CultureInfo.InvariantCulture);
    }

    private static async Task InsertHeaderAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int testataId,
        int numeroCollo,
        int clientId,
        int paymentId,
        string? note,
        DateTime now,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dbo.tabTestateColli
            (
                idTestata,
                IdListino,
                NumeroCollo,
                IdOperatore,
                IdCassa,
                DataCreaz,
                OraCreaz,
                DataScontrino,
                OraScontrino,
                dataAgg,
                UtenteUltimoAccesso,
                Annotazioni,
                NumeroDocumento,
                DataDocumento,
                DDTForn_DataRiferimento,
                FATTForn_DataRiferimento,
                idMagazzinoPartenza,
                idMagazzinoDestinazione,
                IdBanca,
                IdPagamento,
                IdCliente,
                IdDestinazione,
                idCausale,
                idCausaleMagazzino,
                OrdCli_DataRiferimento,
                idAgente,
                DataRichiesta,
                DataConsegna,
                Accettazione,
                DataAccettazione,
                IsElaborato,
                Dest_IsFatturazione,
                TipoDocumentoPrenotato
            )
            VALUES
            (
                @idTestata,
                -1,
                @numeroCollo,
                1,
                0,
                @dataCreaz,
                @oraCreaz,
                @zeroDate,
                @zeroDate,
                @dataAgg,
                N'Scan2Enter',
                @annotazioni,
                0,
                @dataCreaz,
                @zeroDate,
                @zeroDate,
                0,
                -1,
                -1,
                @idPagamento,
                @idCliente,
                -1,
                0,
                0,
                @zeroDate,
                0,
                @zeroDate,
                @zeroDate,
                0,
                @zeroDate,
                0,
                0,
                25
            );
            """;

        await using var command =
            new SqlCommand(sql, connection, transaction);

        command.Parameters.Add("@idTestata", SqlDbType.Int).Value = testataId;

        command.Parameters.Add("@numeroCollo", SqlDbType.NVarChar, 100)
            .Value = numeroCollo.ToString(CultureInfo.InvariantCulture);
        command.Parameters.Add("@dataCreaz", SqlDbType.DateTime)
            .Value = now.Date;

        command.Parameters.Add("@oraCreaz", SqlDbType.DateTime)
            .Value = new DateTime(
                1899, 12, 30,
                now.Hour, now.Minute, now.Second,
                now.Millisecond);

        command.Parameters.Add("@dataAgg", SqlDbType.DateTime).Value = now;
        command.Parameters.Add("@annotazioni", SqlDbType.NVarChar, 4000)
            .Value = string.IsNullOrWhiteSpace(note)
                ? DBNull.Value
                : note.Trim();
        command.Parameters.Add("@zeroDate", SqlDbType.DateTime)
            .Value = new DateTime(1899, 12, 30);
        command.Parameters.Add("@idPagamento", SqlDbType.Int).Value = paymentId;
        command.Parameters.Add("@idCliente", SqlDbType.Int).Value = clientId;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertDetailAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int detailId,
        int testataId,
        PreparedItem item,
        DateTime now,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dbo.tabDettaglioColli
            (
                idDettaglio,
                IdTestata,
                IdListino,
                ListinoGenerico,
                BarCode,
                Descrizione,
                Prezzo,
                Quantita,
                Aiva,
                Totale,
                Sconto1,
                Sconto2,
                Sconto3,
                Sconto4,
                ImportoSconto,
                TotaleNettoSconto,
                Imponibile,
                Iva,
                CodiceIva,
                dataAgg,
                UtenteUltimoAccesso,
                PrezzoNettoIva,
                idArticolo,
                Peso,
                Volume,
                idOperatore_Riga,
                idRiferimentoRigaPadre,
                NoteRiga,
                QtaColli
            )
            VALUES
            (
                @idDettaglio,
                @idTestata,
                @idListino,
                0,
                @barcode,
                @descrizione,
                @prezzo,
                @quantita,
                @aiva,
                @totale,
                @sconto1,
                @sconto2,
                @sconto3,
                @sconto4,
                @importoSconto,
                @totaleNettoSconto,
                @imponibile,
                @iva,
                @codiceIva,
                @dataAgg,
                N'Scan2Enter',
                @prezzoNettoIva,
                @idArticolo,
                0,
                0,
                1,
                -1,
                N'',
                0
            );
            """;

        await using var command =
            new SqlCommand(sql, connection, transaction);

        command.Parameters.Add("@idDettaglio", SqlDbType.Int).Value = detailId;
        command.Parameters.Add("@idTestata", SqlDbType.Int).Value = testataId;
        command.Parameters.Add("@idListino", SqlDbType.Int)
            .Value = item.PriceListId;
        command.Parameters.Add("@idArticolo", SqlDbType.Int)
            .Value = Convert.ToInt32(item.ArticleId);
        command.Parameters.Add("@barcode", SqlDbType.NVarChar, 60)
            .Value = item.Barcode;
        command.Parameters.Add("@descrizione", SqlDbType.NVarChar, 8000)
            .Value = item.Description;

        command.Parameters.Add("@prezzo", SqlDbType.Float)
            .Value = Convert.ToDouble(item.Price);
        command.Parameters.Add("@quantita", SqlDbType.Float)
            .Value = Convert.ToDouble(item.Quantity);
        command.Parameters.Add("@aiva", SqlDbType.Float)
            .Value = Convert.ToDouble(item.VatRate);
        command.Parameters.Add("@totale", SqlDbType.Float)
            .Value = Convert.ToDouble(item.Total);
        command.Parameters.Add("@sconto1", SqlDbType.Float)
            .Value = Convert.ToDouble(item.Discount1);
        command.Parameters.Add("@sconto2", SqlDbType.Float)
            .Value = Convert.ToDouble(item.Discount2);
        command.Parameters.Add("@sconto3", SqlDbType.Float)
            .Value = Convert.ToDouble(item.Discount3);
        command.Parameters.Add("@sconto4", SqlDbType.Float)
            .Value = Convert.ToDouble(item.Discount4);
        command.Parameters.Add("@importoSconto", SqlDbType.Float)
            .Value = Convert.ToDouble(item.DiscountAmount);
        command.Parameters.Add("@totaleNettoSconto", SqlDbType.Float)
            .Value = Convert.ToDouble(item.NetTotal);
        command.Parameters.Add("@imponibile", SqlDbType.Float)
            .Value = Convert.ToDouble(item.Taxable);
        command.Parameters.Add("@iva", SqlDbType.Float)
            .Value = Convert.ToDouble(item.Vat);

        command.Parameters.Add("@codiceIva", SqlDbType.NVarChar, 200)
            .Value = item.VatRate.ToString(
                "0.##",
                CultureInfo.InvariantCulture);

        command.Parameters.Add("@dataAgg", SqlDbType.DateTime).Value = now;

        command.Parameters.Add("@prezzoNettoIva", SqlDbType.Float)
            .Value = Convert.ToDouble(item.NetUnit);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static decimal[] NormalizeDiscounts(
        decimal discount1,
        decimal discount2,
        decimal discount3,
        decimal discount4,
        decimal manualDiscount)
    {
        var values = new[]
        {
            NormalizeDiscount(discount1),
            NormalizeDiscount(discount2),
            NormalizeDiscount(discount3),
            NormalizeDiscount(discount4)
        };

        var manual = NormalizeDiscount(manualDiscount);

        if (manual > 0m)
        {
            var inserted = false;

            for (var i = 0; i < values.Length; i++)
            {
                if (values[i] == 0m)
                {
                    values[i] = manual;
                    inserted = true;
                    break;
                }
            }

            if (!inserted)
            {
                throw new InvalidOperationException(
                    "Impossibile applicare lo sconto manuale: " +
                    "tutti e quattro gli sconti automatici sono già occupati.");
            }
        }

        return values;
    }

    private static decimal NormalizeDiscount(decimal value)
    {
        if (value < 0m || value > 100m)
        {
            throw new InvalidOperationException(
                $"Sconto non valido: {value.ToString(CultureInfo.InvariantCulture)}%. " +
                "Valore ammesso da 0 a 100.");
        }

        return decimal.Round(
            value,
            4,
            MidpointRounding.AwayFromZero);
    }

    private static decimal ApplyDiscounts(
        decimal grossTotal,
        IReadOnlyList<decimal> discounts)
    {
        var net = grossTotal;

        foreach (var discount in discounts)
        {
            if (discount <= 0m)
            {
                continue;
            }

            net = decimal.Round(
                net * (1m - (discount / 100m)),
                2,
                MidpointRounding.AwayFromZero);
        }

        return net;
    }


    private static string BuildColloEan13(int numeroCollo)
    {
        var data12 =
            "240" +
            numeroCollo
                .ToString(CultureInfo.InvariantCulture)
                .PadLeft(9, '0');

        if (data12.Length != 12)
        {
            throw new InvalidOperationException(
                "Numero collo troppo lungo per il barcode EAN-13.");
        }

        var sum = 0;

        for (var i = 0; i < data12.Length; i++)
        {
            var digit = data12[i] - '0';
            sum += i % 2 == 0 ? digit : digit * 3;
        }

        var checkDigit = (10 - (sum % 10)) % 10;

        return data12 + checkDigit.ToString(
            CultureInfo.InvariantCulture);
    }

    private readonly record struct PreparedItem(
        long ArticleId,
        string Barcode,
        string Description,
        int PriceListId,
        decimal Price,
        decimal Quantity,
        decimal VatRate,
        decimal Total,
        decimal Discount1,
        decimal Discount2,
        decimal Discount3,
        decimal Discount4,
        decimal DiscountAmount,
        decimal NetTotal,
        decimal Taxable,
        decimal Vat,
        decimal NetUnit);
}
