using Microsoft.Data.SqlClient;

namespace Scan2EnterGateway.Data;

public sealed class ProductPromoRepository
{
    private const int Scan2EnterPromoId = 3;
    private readonly string _connectionString;

    public ProductPromoRepository(IConfiguration configuration)
    {
        _connectionString =
            configuration.GetConnectionString("DueDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'DueDatabase' is missing.");
    }

    public async Task<ProductPromoDto?> GetAsync(
        long articleId,
        CancellationToken cancellationToken = default)
    {
        if (articleId <= 0)
            return null;

        await using var connection =
            new SqlConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);

        await EnsureMetadataTableAsync(
            connection,
            transaction: null,
            cancellationToken);

        const string sql = """
            SELECT TOP (1)
                m.ArticleId,
                m.DiscountPercent,
                m.PublicPrice,
                m.OfferPrice,
                m.ValidFrom,
                m.ValidTo,
                p.primaryHash,
                m.UpdatedAt
            FROM dbo.Scan2EnterPromoDiscounts AS m
            LEFT JOIN due_prm.tabDettaglioPromoArticoliPrezzoImposto AS p
                ON p.IdPromo = @promoId
               AND p.idArticolo = m.ArticleId
               AND ISNULL(p.idVariante1, -1) = -1
               AND ISNULL(p.idVariante2, -1) = -1
               AND ISNULL(p.idVariante3, -1) = -1
            WHERE m.ArticleId = @articleId;
            """;

        await using var command =
            new SqlCommand(sql, connection);

        command.Parameters.AddWithValue("@promoId", Scan2EnterPromoId);
        command.Parameters.AddWithValue("@articleId", articleId);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new ProductPromoDto
        {
            ArticleId = reader.GetInt64(reader.GetOrdinal("ArticleId")),
            DiscountPercent = reader.GetDecimal(reader.GetOrdinal("DiscountPercent")),
            PublicPrice = reader.GetDecimal(reader.GetOrdinal("PublicPrice")),
            OfferPrice = reader.GetDecimal(reader.GetOrdinal("OfferPrice")),
            ValidFrom =
                reader.IsDBNull(reader.GetOrdinal("ValidFrom"))
                    ? null
                    : reader.GetDateTime(reader.GetOrdinal("ValidFrom")),
            ValidTo =
                reader.IsDBNull(reader.GetOrdinal("ValidTo"))
                    ? null
                    : reader.GetDateTime(reader.GetOrdinal("ValidTo")),
            PrimaryHash =
                reader.IsDBNull(reader.GetOrdinal("primaryHash"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("primaryHash")),
            UpdatedAt = reader.GetDateTime(reader.GetOrdinal("UpdatedAt"))
        };
    }

    public async Task<ProductPromoDto> SetDiscountAsync(
        long articleId,
        decimal discountPercent,
        CancellationToken cancellationToken = default,
        DateTime? validFrom = null,
        DateTime? validTo = null)
    {
        if (articleId <= 0)
            throw new ArgumentOutOfRangeException(nameof(articleId));

        if (discountPercent < 0m || discountPercent > 100m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(discountPercent),
                "Lo sconto deve essere compreso tra 0 e 100.");
        }

        if (validFrom.HasValue && validTo.HasValue && validTo.Value < validFrom.Value)
        {
            throw new ArgumentException(
                "La data di fine promo non può precedere la data di inizio.");
        }

        await using var connection =
            new SqlConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);

        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await EnsureMetadataTableAsync(
                connection,
                (SqlTransaction)transaction,
                cancellationToken);

            var publicPrice =
                await GetPublicPriceAsync(
                    connection,
                    (SqlTransaction)transaction,
                    articleId,
                    cancellationToken);

            if (!publicPrice.HasValue)
            {
                throw new InvalidOperationException(
                    "Prezzo AL PUBBLICO non trovato per l'articolo.");
            }

            var mathematicalPrice =
                publicPrice.Value *
                (1m - discountPercent / 100m);

            var offerPrice =
                RoundToCommercialTenCents(mathematicalPrice);

            var now = DateTime.Now;
            var isActiveNow =
                (!validFrom.HasValue || now >= validFrom.Value) &&
                (!validTo.HasValue || now <= validTo.Value);

            var existingHash =
                await GetExistingHashAsync(
                    connection,
                    (SqlTransaction)transaction,
                    articleId,
                    cancellationToken);

            string? primaryHash = null;

            if (isActiveNow)
            {
                if (!string.IsNullOrWhiteSpace(existingHash))
                {
                    primaryHash = existingHash;

                    const string updateSql = """
                        UPDATE due_prm.tabDettaglioPromoArticoliPrezzoImposto
                        SET
                            PrezzoOfferta = @offerPrice,
                            DataAgg = GETDATE()
                        WHERE IdPromo = @promoId
                          AND idArticolo = @articleId
                          AND ISNULL(idVariante1, -1) = -1
                          AND ISNULL(idVariante2, -1) = -1
                          AND ISNULL(idVariante3, -1) = -1;
                        """;

                    await using var updateCommand =
                        new SqlCommand(
                            updateSql,
                            connection,
                            (SqlTransaction)transaction);

                    updateCommand.Parameters.AddWithValue("@offerPrice", offerPrice);
                    updateCommand.Parameters.AddWithValue("@promoId", Scan2EnterPromoId);
                    updateCommand.Parameters.AddWithValue("@articleId", articleId);

                    await updateCommand.ExecuteNonQueryAsync(cancellationToken);
                }
                else
                {
                    var newDetailId =
                        await GetNewDetailIdAsync(
                            connection,
                            (SqlTransaction)transaction,
                            cancellationToken);

                    primaryHash =
                        newDetailId.ToString(
                            System.Globalization.CultureInfo.InvariantCulture);

                    const string insertSql = """
                        INSERT INTO due_prm.tabDettaglioPromoArticoliPrezzoImposto
                        (
                            IdPromo,
                            PrezzoOfferta,
                            primaryHash,
                            DataScadenza,
                            Barcode,
                            idArticolo,
                            idVariante1,
                            idVariante2,
                            idVariante3,
                            QuantitaMassima,
                            DataAgg,
                            idRimborsoPromozionale,
                            importoRimborsoPromozionale
                        )
                        VALUES
                        (
                            @promoId,
                            @offerPrice,
                            @primaryHash,
                            NULL,
                            N'',
                            @articleId,
                            -1,
                            -1,
                            -1,
                            0,
                            GETDATE(),
                            -1,
                            0
                        );
                        """;

                    await using var insertCommand =
                        new SqlCommand(
                            insertSql,
                            connection,
                            (SqlTransaction)transaction);

                    insertCommand.Parameters.AddWithValue("@promoId", Scan2EnterPromoId);
                    insertCommand.Parameters.AddWithValue("@offerPrice", offerPrice);
                    insertCommand.Parameters.AddWithValue("@primaryHash", primaryHash);
                    insertCommand.Parameters.AddWithValue("@articleId", articleId);

                    await insertCommand.ExecuteNonQueryAsync(cancellationToken);
                }
            }
            else
            {
                const string removeDueSql = """
                    DELETE FROM due_prm.tabDettaglioPromoArticoliPrezzoImposto
                    WHERE IdPromo = @promoId
                      AND idArticolo = @articleId
                      AND ISNULL(idVariante1, -1) = -1
                      AND ISNULL(idVariante2, -1) = -1
                      AND ISNULL(idVariante3, -1) = -1;
                    """;

                await using var removeDueCommand =
                    new SqlCommand(
                        removeDueSql,
                        connection,
                        (SqlTransaction)transaction);

                removeDueCommand.Parameters.AddWithValue("@promoId", Scan2EnterPromoId);
                removeDueCommand.Parameters.AddWithValue("@articleId", articleId);

                await removeDueCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            const string metadataSql = """
                UPDATE dbo.Scan2EnterPromoDiscounts
                SET
                    DiscountPercent = @discountPercent,
                    PublicPrice = @publicPrice,
                    OfferPrice = @offerPrice,
                    ValidFrom = @validFrom,
                    ValidTo = @validTo,
                    UpdatedAt = GETDATE()
                WHERE ArticleId = @articleId;

                IF @@ROWCOUNT = 0
                BEGIN
                    INSERT INTO dbo.Scan2EnterPromoDiscounts
                    (
                        ArticleId,
                        DiscountPercent,
                        PublicPrice,
                        OfferPrice,
                        ValidFrom,
                        ValidTo,
                        UpdatedAt
                    )
                    VALUES
                    (
                        @articleId,
                        @discountPercent,
                        @publicPrice,
                        @offerPrice,
                        @validFrom,
                        @validTo,
                        GETDATE()
                    );
                END;
                """;

            await using var metadataCommand =
                new SqlCommand(
                    metadataSql,
                    connection,
                    (SqlTransaction)transaction);

            metadataCommand.Parameters.AddWithValue("@articleId", articleId);
            metadataCommand.Parameters.AddWithValue("@discountPercent", discountPercent);
            metadataCommand.Parameters.AddWithValue("@publicPrice", publicPrice.Value);
            metadataCommand.Parameters.AddWithValue("@offerPrice", offerPrice);
            metadataCommand.Parameters.Add(
                "@validFrom",
                System.Data.SqlDbType.DateTime).Value =
                validFrom.HasValue ? validFrom.Value : DBNull.Value;
            metadataCommand.Parameters.Add(
                "@validTo",
                System.Data.SqlDbType.DateTime).Value =
                validTo.HasValue ? validTo.Value : DBNull.Value;

            await metadataCommand.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return new ProductPromoDto
            {
                ArticleId = articleId,
                DiscountPercent = discountPercent,
                PublicPrice = publicPrice.Value,
                OfferPrice = offerPrice,
                ValidFrom = validFrom,
                ValidTo = validTo,
                PrimaryHash = primaryHash,
                UpdatedAt = DateTime.Now
            };
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<ProductPromoListItemDto>> GetListAsync(
        string? search = null,
        string? status = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await EnsureMetadataTableAsync(connection, transaction: null, cancellationToken);

        var normalizedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var normalizedStatus = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToUpperInvariant();

        const string sql = """
            SELECT
                m.ArticleId,
                a.CodiceArticolo AS Codice,
                a.Descrizione,
                (
                    SELECT TOP (1)
                        LTRIM(RTRIM(b.Barcode))
                    FROM dbo.tabBarcode AS b
                    WHERE b.idArticolo = m.ArticleId
                      AND NULLIF(LTRIM(RTRIM(b.Barcode)), '') IS NOT NULL
                    ORDER BY
                        CASE
                            WHEN LEN(LTRIM(RTRIM(b.Barcode))) = 13 THEN 0
                            ELSE 1
                        END,
                        b.Barcode
                ) AS Barcode,
                m.DiscountPercent,
                m.PublicPrice,
                m.OfferPrice,
                m.ValidFrom,
                m.ValidTo,
                p.primaryHash,
                m.UpdatedAt
            FROM dbo.Scan2EnterPromoDiscounts AS m
            LEFT JOIN dbo.tabArticoli AS a
                ON a.IdArticolo = m.ArticleId
            LEFT JOIN due_prm.tabDettaglioPromoArticoliPrezzoImposto AS p
                ON p.IdPromo = @promoId
               AND p.idArticolo = m.ArticleId
               AND ISNULL(p.idVariante1, -1) = -1
               AND ISNULL(p.idVariante2, -1) = -1
               AND ISNULL(p.idVariante3, -1) = -1
            WHERE
                @search IS NULL
                OR CAST(m.ArticleId AS nvarchar(30)) LIKE '%' + @search + '%'
                OR ISNULL(a.CodiceArticolo, '') LIKE '%' + @search + '%'
                OR ISNULL(a.Descrizione, '') LIKE '%' + @search + '%'
                OR EXISTS
                (
                    SELECT 1
                    FROM dbo.tabBarcode AS bSearch
                    WHERE bSearch.idArticolo = m.ArticleId
                      AND ISNULL(bSearch.Barcode, '') LIKE '%' + @search + '%'
                )
            ORDER BY
                CASE
                    WHEN (m.ValidFrom IS NULL OR m.ValidFrom <= GETDATE())
                     AND (m.ValidTo IS NULL OR m.ValidTo >= GETDATE()) THEN 0
                    WHEN m.ValidFrom > GETDATE() THEN 1
                    ELSE 2
                END,
                m.ValidTo,
                m.ArticleId;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@promoId", Scan2EnterPromoId);
        command.Parameters.Add("@search", System.Data.SqlDbType.NVarChar, 200).Value =
            normalizedSearch is null ? DBNull.Value : normalizedSearch;

        var results = new List<ProductPromoListItemDto>();
        var now = DateTime.Now;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var validFrom = reader.IsDBNull(reader.GetOrdinal("ValidFrom"))
                ? (DateTime?)null
                : reader.GetDateTime(reader.GetOrdinal("ValidFrom"));
            var validTo = reader.IsDBNull(reader.GetOrdinal("ValidTo"))
                ? (DateTime?)null
                : reader.GetDateTime(reader.GetOrdinal("ValidTo"));

            var promoStatus =
                validFrom.HasValue && validFrom.Value > now ? "PROGRAMMATA" :
                validTo.HasValue && validTo.Value < now ? "SCADUTA" :
                validTo.HasValue ? "IN_CORSO" : "SENZA_SCADENZA";

            if (normalizedStatus is not null &&
                !string.Equals(normalizedStatus, promoStatus, StringComparison.OrdinalIgnoreCase) &&
                !(normalizedStatus == "ATTIVE" &&
                  (promoStatus == "IN_CORSO" || promoStatus == "SENZA_SCADENZA")))
            {
                continue;
            }

            results.Add(new ProductPromoListItemDto
            {
                ArticleId = reader.GetInt64(reader.GetOrdinal("ArticleId")),
                Code = reader.IsDBNull(reader.GetOrdinal("Codice")) ? null : reader.GetString(reader.GetOrdinal("Codice")),
                Description = reader.IsDBNull(reader.GetOrdinal("Descrizione")) ? null : reader.GetString(reader.GetOrdinal("Descrizione")),
                Barcode = reader.IsDBNull(reader.GetOrdinal("Barcode")) ? null : reader.GetString(reader.GetOrdinal("Barcode")),
                DiscountPercent = reader.GetDecimal(reader.GetOrdinal("DiscountPercent")),
                PublicPrice = reader.GetDecimal(reader.GetOrdinal("PublicPrice")),
                OfferPrice = reader.GetDecimal(reader.GetOrdinal("OfferPrice")),
                ValidFrom = validFrom,
                ValidTo = validTo,
                Status = promoStatus,
                IsActive = promoStatus == "IN_CORSO" || promoStatus == "SENZA_SCADENZA",
                PrimaryHash = reader.IsDBNull(reader.GetOrdinal("primaryHash")) ? null : reader.GetString(reader.GetOrdinal("primaryHash")),
                UpdatedAt = reader.GetDateTime(reader.GetOrdinal("UpdatedAt"))
            });
        }

        return results;
    }

    public async Task<int> ReconcileValidityAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            new SqlConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);

        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await EnsureMetadataTableAsync(
                connection,
                (SqlTransaction)transaction,
                cancellationToken);

            const string metadataSql = """
                SELECT
                    ArticleId,
                    OfferPrice,
                    ValidFrom,
                    ValidTo
                FROM dbo.Scan2EnterPromoDiscounts;
                """;

            var promos =
                new List<(long ArticleId, decimal OfferPrice, DateTime? ValidFrom, DateTime? ValidTo)>();

            await using (var metadataCommand =
                new SqlCommand(
                    metadataSql,
                    connection,
                    (SqlTransaction)transaction))
            await using (var reader =
                await metadataCommand.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    promos.Add(
                        (
                            reader.GetInt64(reader.GetOrdinal("ArticleId")),
                            reader.GetDecimal(reader.GetOrdinal("OfferPrice")),
                            reader.IsDBNull(reader.GetOrdinal("ValidFrom"))
                                ? null
                                : reader.GetDateTime(reader.GetOrdinal("ValidFrom")),
                            reader.IsDBNull(reader.GetOrdinal("ValidTo"))
                                ? null
                                : reader.GetDateTime(reader.GetOrdinal("ValidTo"))
                        ));
                }
            }

            var now = DateTime.Now;
            var changed = 0;

            foreach (var promo in promos)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var isActiveNow =
                    (!promo.ValidFrom.HasValue || now >= promo.ValidFrom.Value) &&
                    (!promo.ValidTo.HasValue || now <= promo.ValidTo.Value);

                var existingHash =
                    await GetExistingHashAsync(
                        connection,
                        (SqlTransaction)transaction,
                        promo.ArticleId,
                        cancellationToken);

                if (isActiveNow)
                {
                    if (!string.IsNullOrWhiteSpace(existingHash))
                    {
                        const string updateSql = """
                            UPDATE due_prm.tabDettaglioPromoArticoliPrezzoImposto
                            SET
                                PrezzoOfferta = @offerPrice,
                                DataAgg = GETDATE()
                            WHERE IdPromo = @promoId
                              AND idArticolo = @articleId
                              AND ISNULL(idVariante1, -1) = -1
                              AND ISNULL(idVariante2, -1) = -1
                              AND ISNULL(idVariante3, -1) = -1
                              AND (
                                    PrezzoOfferta IS NULL
                                    OR ABS(PrezzoOfferta - @offerPrice) > 0.0001
                                  );
                            """;

                        await using var updateCommand =
                            new SqlCommand(
                                updateSql,
                                connection,
                                (SqlTransaction)transaction);

                        updateCommand.Parameters.AddWithValue("@offerPrice", promo.OfferPrice);
                        updateCommand.Parameters.AddWithValue("@promoId", Scan2EnterPromoId);
                        updateCommand.Parameters.AddWithValue("@articleId", promo.ArticleId);

                        changed +=
                            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
                    }
                    else
                    {
                        var newDetailId =
                            await GetNewDetailIdAsync(
                                connection,
                                (SqlTransaction)transaction,
                                cancellationToken);

                        var primaryHash =
                            newDetailId.ToString(
                                System.Globalization.CultureInfo.InvariantCulture);

                        const string insertSql = """
                            INSERT INTO due_prm.tabDettaglioPromoArticoliPrezzoImposto
                            (
                                IdPromo,
                                PrezzoOfferta,
                                primaryHash,
                                DataScadenza,
                                Barcode,
                                idArticolo,
                                idVariante1,
                                idVariante2,
                                idVariante3,
                                QuantitaMassima,
                                DataAgg,
                                idRimborsoPromozionale,
                                importoRimborsoPromozionale
                            )
                            VALUES
                            (
                                @promoId,
                                @offerPrice,
                                @primaryHash,
                                NULL,
                                N'',
                                @articleId,
                                -1,
                                -1,
                                -1,
                                0,
                                GETDATE(),
                                -1,
                                0
                            );
                            """;

                        await using var insertCommand =
                            new SqlCommand(
                                insertSql,
                                connection,
                                (SqlTransaction)transaction);

                        insertCommand.Parameters.AddWithValue("@promoId", Scan2EnterPromoId);
                        insertCommand.Parameters.AddWithValue("@offerPrice", promo.OfferPrice);
                        insertCommand.Parameters.AddWithValue("@primaryHash", primaryHash);
                        insertCommand.Parameters.AddWithValue("@articleId", promo.ArticleId);

                        changed +=
                            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(existingHash))
                {
                    const string deleteSql = """
                        DELETE FROM due_prm.tabDettaglioPromoArticoliPrezzoImposto
                        WHERE IdPromo = @promoId
                          AND idArticolo = @articleId
                          AND ISNULL(idVariante1, -1) = -1
                          AND ISNULL(idVariante2, -1) = -1
                          AND ISNULL(idVariante3, -1) = -1;
                        """;

                    await using var deleteCommand =
                        new SqlCommand(
                            deleteSql,
                            connection,
                            (SqlTransaction)transaction);

                    deleteCommand.Parameters.AddWithValue("@promoId", Scan2EnterPromoId);
                    deleteCommand.Parameters.AddWithValue("@articleId", promo.ArticleId);

                    changed +=
                        await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            await transaction.CommitAsync(cancellationToken);
            return changed;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<bool> RemoveAsync(
        long articleId,
        CancellationToken cancellationToken = default)
    {
        if (articleId <= 0)
            return false;

        await using var connection =
            new SqlConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);

        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await EnsureMetadataTableAsync(
                connection,
                (SqlTransaction)transaction,
                cancellationToken);

            const string sql = """
                DELETE FROM due_prm.tabDettaglioPromoArticoliPrezzoImposto
                WHERE IdPromo = @promoId
                  AND idArticolo = @articleId
                  AND ISNULL(idVariante1, -1) = -1
                  AND ISNULL(idVariante2, -1) = -1
                  AND ISNULL(idVariante3, -1) = -1;

                DECLARE @affected int = @@ROWCOUNT;

                DELETE FROM dbo.Scan2EnterPromoDiscounts
                WHERE ArticleId = @articleId;

                SELECT @affected;
                """;

            await using var command =
                new SqlCommand(
                    sql,
                    connection,
                    (SqlTransaction)transaction);

            command.Parameters.AddWithValue("@promoId", Scan2EnterPromoId);
            command.Parameters.AddWithValue("@articleId", articleId);

            var result =
                await command.ExecuteScalarAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return Convert.ToInt32(result) > 0;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static decimal RoundToCommercialTenCents(decimal value)
    {
        return
            Math.Round(
                value * 10m,
                0,
                MidpointRounding.AwayFromZero) /
            10m;
    }

    private static async Task EnsureMetadataTableAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            IF OBJECT_ID(
                N'dbo.Scan2EnterPromoDiscounts',
                N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.Scan2EnterPromoDiscounts
                (
                    ArticleId bigint NOT NULL
                        CONSTRAINT PK_Scan2EnterPromoDiscounts
                        PRIMARY KEY,
                    DiscountPercent decimal(9,4) NOT NULL,
                    PublicPrice decimal(18,2) NOT NULL,
                    OfferPrice decimal(18,2) NOT NULL,
                    ValidFrom datetime NULL,
                    ValidTo datetime NULL,
                    UpdatedAt datetime NOT NULL
                        CONSTRAINT DF_Scan2EnterPromoDiscounts_UpdatedAt
                        DEFAULT GETDATE()
                );
            END;

            IF COL_LENGTH('dbo.Scan2EnterPromoDiscounts', 'ValidFrom') IS NULL
            BEGIN
                ALTER TABLE dbo.Scan2EnterPromoDiscounts
                ADD ValidFrom datetime NULL;
            END;

            IF COL_LENGTH('dbo.Scan2EnterPromoDiscounts', 'ValidTo') IS NULL
            BEGIN
                ALTER TABLE dbo.Scan2EnterPromoDiscounts
                ADD ValidTo datetime NULL;
            END;
            """;

        await using var command =
            new SqlCommand(sql, connection, transaction);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<decimal?> GetPublicPriceAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long articleId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1)
                CAST(pv.PrezzoVendita AS decimal(18,2))
            FROM dbo.tabPrezziVendita AS pv
            WHERE pv.IdArticolo = @articleId
              AND pv.IdListino = 1
              AND ISNULL(pv.idVariante1, -1) = -1
              AND ISNULL(pv.idVariante2, -1) = -1
              AND ISNULL(pv.idVariante3, -1) = -1
            ORDER BY pv.DataAgg DESC;
            """;

        await using var command =
            new SqlCommand(sql, connection, transaction);

        command.Parameters.AddWithValue("@articleId", articleId);

        var value =
            await command.ExecuteScalarAsync(cancellationToken);

        return value == null || value == DBNull.Value
            ? null
            : Convert.ToDecimal(value);
    }

    private static async Task<string?> GetExistingHashAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long articleId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1)
                primaryHash
            FROM due_prm.tabDettaglioPromoArticoliPrezzoImposto
            WHERE IdPromo = @promoId
              AND idArticolo = @articleId
              AND ISNULL(idVariante1, -1) = -1
              AND ISNULL(idVariante2, -1) = -1
              AND ISNULL(idVariante3, -1) = -1
            ORDER BY DataAgg DESC;
            """;

        await using var command =
            new SqlCommand(sql, connection, transaction);

        command.Parameters.AddWithValue("@promoId", Scan2EnterPromoId);
        command.Parameters.AddWithValue("@articleId", articleId);

        var value =
            await command.ExecuteScalarAsync(cancellationToken);

        return value == null || value == DBNull.Value
            ? null
            : Convert.ToString(value);
    }

    private static async Task<int> GetNewDetailIdAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command =
            new SqlCommand(
                "dbo.GetNewId",
                connection,
                transaction);

        command.CommandType =
            System.Data.CommandType.StoredProcedure;

        command.Parameters.AddWithValue("@schemaName", "due_prm");
        command.Parameters.AddWithValue("@tableName", "tabDettaglioPromoArticoli");
        command.Parameters.AddWithValue("@columnName", "idDettaglio");
        command.Parameters.AddWithValue("@checkConflictOnSourceTable", true);

        var output =
            command.Parameters.Add(
                "@newId",
                System.Data.SqlDbType.Int);

        output.Direction =
            System.Data.ParameterDirection.Output;

        await command.ExecuteNonQueryAsync(cancellationToken);

        if (output.Value == DBNull.Value)
        {
            throw new InvalidOperationException(
                "Due Retail non ha restituito un nuovo idDettaglio.");
        }

        return Convert.ToInt32(output.Value);
    }
}

public sealed class ProductPromoDto
{
    public long ArticleId { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal PublicPrice { get; set; }
    public decimal OfferPrice { get; set; }
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
    public string? PrimaryHash { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class ProductPromoListItemDto
{
    public long ArticleId { get; set; }
    public string? Code { get; set; }
    public string? Description { get; set; }
    public string? Barcode { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal PublicPrice { get; set; }
    public decimal OfferPrice { get; set; }
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
    public string Status { get; set; } = "";
    public bool IsActive { get; set; }
    public string? PrimaryHash { get; set; }
    public DateTime UpdatedAt { get; set; }
}
