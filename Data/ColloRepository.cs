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
                var lineTotal = decimal.Round(
                    item.Price * item.Quantity,
                    2,
                    MidpointRounding.AwayFromZero);

                var divisor = 1m + (vatRate / 100m);

                var taxableTotal = decimal.Round(
                    lineTotal / divisor,
                    2,
                    MidpointRounding.AwayFromZero);

                var vatTotal = lineTotal - taxableTotal;

                var netUnit = divisor == 0m
                    ? item.Price
                    : decimal.Round(
                        item.Price / divisor,
                        4,
                        MidpointRounding.AwayFromZero);

                preparedItems.Add(new PreparedItem(
                    Barcode: item.Barcode.Trim(),
                    Description: article.Value.Description,
                    Price: item.Price,
                    Quantity: item.Quantity,
                    VatRate: vatRate,
                    Total: lineTotal,
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
                Total = preparedItems.Sum(x => x.Total),
                CreatedAt = now
            };
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
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

    private static async Task<(string Description, decimal VatRate)?>
        ReadArticleAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            string barcode,
            CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1)
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
            reader.IsDBNull(0) ? "" : reader.GetString(0).Trim(),
            reader.IsDBNull(1)
                ? 0m
                : Convert.ToDecimal(
                    reader.GetValue(1),
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
                -1,
                0,
                @barcode,
                @descrizione,
                @prezzo,
                @quantita,
                @aiva,
                @totale,
                0,
                0,
                0,
                0,
                0,
                @totale,
                @imponibile,
                @iva,
                @codiceIva,
                @dataAgg,
                N'Scan2Enter',
                @prezzoNettoIva,
                0,
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
        string Barcode,
        string Description,
        decimal Price,
        decimal Quantity,
        decimal VatRate,
        decimal Total,
        decimal Taxable,
        decimal Vat,
        decimal NetUnit);
}