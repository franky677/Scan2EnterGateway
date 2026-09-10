using Microsoft.Data.SqlClient;

namespace Scan2EnterGateway.Data;

public sealed class ProductPromoRepository
{
    private const int Scan2EnterPromoId = 3;
    private const string ProducerGroupType = "PRODUTTORE";
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

    public async Task<ProductPromoDto?> GetEffectiveAsync(
        long articleId,
        CancellationToken cancellationToken = default)
    {
        if (articleId <= 0)
            return null;

        // Precedenza assoluta alla promo individuale, ma solo se attiva adesso.
        var individualPromo = await GetAsync(articleId, cancellationToken);
        var now = DateTime.Now;

        if (individualPromo is not null &&
            (!individualPromo.ValidFrom.HasValue || now >= individualPromo.ValidFrom.Value) &&
            (!individualPromo.ValidTo.HasValue || now <= individualPromo.ValidTo.Value))
        {
            return individualPromo;
        }

        // Se non c'e' una promo individuale attiva, cerchiamo una promo marca
        // realmente materializzata per l'articolo. Il match sull'hash Due evita
        // di considerare tracking vecchi o una seconda promo di gruppo in conflitto.
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT TOP (1)
                gi.ArticleId,
                g.DiscountPercent,
                gi.PublicPrice,
                gi.OfferPrice,
                g.ValidFrom,
                g.ValidTo,
                p.primaryHash,
                gi.UpdatedAt
            FROM dbo.Scan2EnterPromoGroupItems AS gi
            INNER JOIN dbo.Scan2EnterPromoGroups AS g
                ON g.IdPromoGroup = gi.IdPromoGroup
            INNER JOIN due_prm.tabDettaglioPromoArticoliPrezzoImposto AS p
                ON p.IdPromo = @promoId
               AND p.idArticolo = gi.ArticleId
               AND p.primaryHash = gi.DuePrimaryHash
               AND ISNULL(p.idVariante1, -1) = -1
               AND ISNULL(p.idVariante2, -1) = -1
               AND ISNULL(p.idVariante3, -1) = -1
            WHERE gi.ArticleId = @articleId
              AND gi.Materialized = 1
              AND g.Enabled = 1
              AND g.GroupType = @groupType
              AND (g.ValidFrom IS NULL OR g.ValidFrom <= @now)
              AND (g.ValidTo IS NULL OR g.ValidTo >= @now)
            ORDER BY g.UpdatedAt DESC, g.IdPromoGroup DESC;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@promoId", Scan2EnterPromoId);
        command.Parameters.AddWithValue("@articleId", articleId);
        command.Parameters.AddWithValue("@groupType", ProducerGroupType);
        command.Parameters.AddWithValue("@now", now);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new ProductPromoDto
        {
            ArticleId = reader.GetInt64(reader.GetOrdinal("ArticleId")),
            DiscountPercent = reader.GetDecimal(reader.GetOrdinal("DiscountPercent")),
            PublicPrice = reader.GetDecimal(reader.GetOrdinal("PublicPrice")),
            OfferPrice = reader.GetDecimal(reader.GetOrdinal("OfferPrice")),
            ValidFrom = reader.IsDBNull(reader.GetOrdinal("ValidFrom"))
                ? null
                : reader.GetDateTime(reader.GetOrdinal("ValidFrom")),
            ValidTo = reader.IsDBNull(reader.GetOrdinal("ValidTo"))
                ? null
                : reader.GetDateTime(reader.GetOrdinal("ValidTo")),
            PrimaryHash = reader.IsDBNull(reader.GetOrdinal("primaryHash"))
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

    public async Task<IReadOnlyList<ProductPromoGroupDto>> GetGroupListAsync(
        string? search = null,
        string? status = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var normalizedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var normalizedStatus = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToUpperInvariant();

        const string sql = """
            SELECT
                g.IdPromoGroup,
                g.GroupType,
                g.GroupId,
                g.GroupCode,
                g.GroupDescription,
                g.DiscountPercent,
                g.ValidFrom,
                g.ValidTo,
                g.Enabled,
                g.CreatedAt,
                g.UpdatedAt,
                CASE
                    WHEN g.GroupType = 'PRODUTTORE' THEN
                    (
                        SELECT COUNT_BIG(*)
                        FROM dbo.tabArticoli AS a
                        OUTER APPLY
                        (
                            SELECT TOP (1)
                                CAST(pv.PrezzoVendita AS decimal(18,2)) AS PublicPrice
                            FROM dbo.tabPrezziVendita AS pv
                            WHERE pv.IdArticolo = a.IdArticolo
                              AND pv.IdListino = 1
                              AND ISNULL(pv.idVariante1, -1) = -1
                              AND ISNULL(pv.idVariante2, -1) = -1
                              AND ISNULL(pv.idVariante3, -1) = -1
                            ORDER BY pv.DataAgg DESC
                        ) AS price
                        WHERE a.IdProduttore = g.GroupId
                          AND ISNULL(a.Attivo, 0) = 1
                          AND price.PublicPrice > 0
                    )
                    ELSE 0
                END AS EligibleArticles,
                (
                    SELECT COUNT_BIG(*)
                    FROM dbo.Scan2EnterPromoGroupItems AS gi
                    WHERE gi.IdPromoGroup = g.IdPromoGroup
                      AND gi.Materialized = 1
                ) AS MaterializedArticles
            FROM dbo.Scan2EnterPromoGroups AS g
            WHERE
                @search IS NULL
                OR ISNULL(g.GroupCode, '') LIKE '%' + @search + '%'
                OR ISNULL(g.GroupDescription, '') LIKE '%' + @search + '%'
                OR CAST(g.GroupId AS nvarchar(30)) LIKE '%' + @search + '%'
            ORDER BY
                CASE WHEN g.Enabled = 1 THEN 0 ELSE 1 END,
                g.ValidTo,
                g.IdPromoGroup;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@search", System.Data.SqlDbType.NVarChar, 200).Value =
            normalizedSearch is null ? DBNull.Value : normalizedSearch;

        var now = DateTime.Now;
        var results = new List<ProductPromoGroupDto>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var validFrom = reader.IsDBNull(reader.GetOrdinal("ValidFrom"))
                ? (DateTime?)null
                : reader.GetDateTime(reader.GetOrdinal("ValidFrom"));
            var validTo = reader.IsDBNull(reader.GetOrdinal("ValidTo"))
                ? (DateTime?)null
                : reader.GetDateTime(reader.GetOrdinal("ValidTo"));
            var enabled = reader.GetBoolean(reader.GetOrdinal("Enabled"));

            var promoStatus = !enabled ? "DISABILITATA" :
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

            results.Add(new ProductPromoGroupDto
            {
                IdPromoGroup = reader.GetInt64(reader.GetOrdinal("IdPromoGroup")),
                GroupType = reader.GetString(reader.GetOrdinal("GroupType")),
                GroupId = reader.GetInt64(reader.GetOrdinal("GroupId")),
                GroupCode = reader.IsDBNull(reader.GetOrdinal("GroupCode")) ? null : reader.GetString(reader.GetOrdinal("GroupCode")),
                GroupDescription = reader.GetString(reader.GetOrdinal("GroupDescription")),
                DiscountPercent = reader.GetDecimal(reader.GetOrdinal("DiscountPercent")),
                ValidFrom = validFrom,
                ValidTo = validTo,
                Enabled = enabled,
                Status = promoStatus,
                EligibleArticles = Convert.ToInt32(reader.GetInt64(reader.GetOrdinal("EligibleArticles"))),
                MaterializedArticles = Convert.ToInt32(reader.GetInt64(reader.GetOrdinal("MaterializedArticles"))),
                CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
                UpdatedAt = reader.GetDateTime(reader.GetOrdinal("UpdatedAt"))
            });
        }

        return results;
    }


    public async Task<IReadOnlyList<ProductPromoProducerOptionDto>> GetProducerOptionsAsync(
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var normalizedSearch =
            string.IsNullOrWhiteSpace(search)
                ? null
                : search.Trim();

        const string sql = """
            SELECT
                p.IdProduttore,
                NULLIF(LTRIM(RTRIM(p.CodiceProduttore)), '') AS CodiceProduttore,
                LTRIM(RTRIM(p.Produttore)) AS Produttore,
                COUNT_BIG(a.IdArticolo) AS EligibleArticles
            FROM dbo.tabProduttori AS p
            INNER JOIN dbo.tabArticoli AS a
                ON a.IdProduttore = p.IdProduttore
               AND ISNULL(a.Attivo, 0) = 1
            OUTER APPLY
            (
                SELECT TOP (1)
                    CAST(pv.PrezzoVendita AS decimal(18,2)) AS PublicPrice
                FROM dbo.tabPrezziVendita AS pv
                WHERE pv.IdArticolo = a.IdArticolo
                  AND pv.IdListino = 1
                  AND ISNULL(pv.idVariante1, -1) = -1
                  AND ISNULL(pv.idVariante2, -1) = -1
                  AND ISNULL(pv.idVariante3, -1) = -1
                ORDER BY pv.DataAgg DESC
            ) AS price
            WHERE price.PublicPrice > 0
              AND (
                    @search IS NULL
                    OR CAST(p.IdProduttore AS nvarchar(30)) LIKE '%' + @search + '%'
                    OR ISNULL(p.CodiceProduttore, '') LIKE '%' + @search + '%'
                    OR ISNULL(p.Produttore, '') LIKE '%' + @search + '%'
                  )
            GROUP BY
                p.IdProduttore,
                p.CodiceProduttore,
                p.Produttore
            HAVING COUNT_BIG(a.IdArticolo) > 0
            ORDER BY
                LTRIM(RTRIM(p.Produttore)),
                p.IdProduttore;
            """;

        await using var command =
            new SqlCommand(sql, connection);

        command.Parameters.Add(
            "@search",
            System.Data.SqlDbType.NVarChar,
            200).Value =
            normalizedSearch is null
                ? DBNull.Value
                : normalizedSearch;

        var results =
            new List<ProductPromoProducerOptionDto>();

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(
                new ProductPromoProducerOptionDto
                {
                    ProducerId =
                        Convert.ToInt64(
                            reader.GetValue(
                                reader.GetOrdinal("IdProduttore"))),
                    ProducerCode =
                        reader.IsDBNull(
                            reader.GetOrdinal("CodiceProduttore"))
                            ? null
                            : reader.GetString(
                                reader.GetOrdinal("CodiceProduttore")),
                    ProducerDescription =
                        reader.IsDBNull(
                            reader.GetOrdinal("Produttore"))
                            ? ""
                            : reader.GetString(
                                reader.GetOrdinal("Produttore")),
                    EligibleArticles =
                        Convert.ToInt32(
                            reader.GetInt64(
                                reader.GetOrdinal("EligibleArticles")))
                });
        }

        return results;
    }

    public async Task<ProductPromoGroupDto> SaveProducerGroupAsync(
        long? idPromoGroup,
        long producerId,
        decimal discountPercent,
        DateTime? validFrom,
        DateTime? validTo,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        if (producerId <= 0)
            throw new ArgumentOutOfRangeException(nameof(producerId));

        if (discountPercent < 0m || discountPercent > 100m)
            throw new ArgumentOutOfRangeException(nameof(discountPercent), "Lo sconto deve essere compreso tra 0 e 100.");

        if (validFrom.HasValue && validTo.HasValue && validTo.Value < validFrom.Value)
            throw new ArgumentException("La data di fine promo non può precedere la data di inizio.");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            const string producerSql = """
                SELECT TOP (1)
                    CodiceProduttore,
                    Produttore
                FROM dbo.tabProduttori
                WHERE IdProduttore = @producerId;
                """;

            string? producerCode;
            string producerDescription;

            await using (var producerCommand = new SqlCommand(producerSql, connection, (SqlTransaction)transaction))
            {
                producerCommand.Parameters.AddWithValue("@producerId", producerId);
                await using var reader = await producerCommand.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                    throw new InvalidOperationException("Produttore non trovato.");

                producerCode = reader.IsDBNull(reader.GetOrdinal("CodiceProduttore"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("CodiceProduttore"));
                producerDescription = reader.IsDBNull(reader.GetOrdinal("Produttore"))
                    ? $"Produttore {producerId}"
                    : reader.GetString(reader.GetOrdinal("Produttore"));
            }

            long savedId;
            if (idPromoGroup.HasValue && idPromoGroup.Value > 0)
            {
                const string updateSql = """
                    UPDATE dbo.Scan2EnterPromoGroups
                    SET
                        GroupType = @groupType,
                        GroupId = @groupId,
                        GroupCode = @groupCode,
                        GroupDescription = @groupDescription,
                        DiscountPercent = @discountPercent,
                        ValidFrom = @validFrom,
                        ValidTo = @validTo,
                        Enabled = @enabled,
                        UpdatedAt = GETDATE()
                    WHERE IdPromoGroup = @idPromoGroup;

                    SELECT @@ROWCOUNT;
                    """;

                await using var updateCommand = new SqlCommand(updateSql, connection, (SqlTransaction)transaction);
                updateCommand.Parameters.AddWithValue("@idPromoGroup", idPromoGroup.Value);
                AddGroupParameters(updateCommand, producerId, producerCode, producerDescription, discountPercent, validFrom, validTo, enabled);
                var affected = Convert.ToInt32(await updateCommand.ExecuteScalarAsync(cancellationToken));
                if (affected == 0)
                    throw new InvalidOperationException("Promo di gruppo non trovata.");

                savedId = idPromoGroup.Value;
            }
            else
            {
                const string insertSql = """
                    INSERT INTO dbo.Scan2EnterPromoGroups
                    (
                        GroupType,
                        GroupId,
                        GroupCode,
                        GroupDescription,
                        DiscountPercent,
                        ValidFrom,
                        ValidTo,
                        Enabled,
                        CreatedAt,
                        UpdatedAt
                    )
                    VALUES
                    (
                        @groupType,
                        @groupId,
                        @groupCode,
                        @groupDescription,
                        @discountPercent,
                        @validFrom,
                        @validTo,
                        @enabled,
                        GETDATE(),
                        GETDATE()
                    );

                    SELECT CAST(SCOPE_IDENTITY() AS bigint);
                    """;

                await using var insertCommand = new SqlCommand(insertSql, connection, (SqlTransaction)transaction);
                AddGroupParameters(insertCommand, producerId, producerCode, producerDescription, discountPercent, validFrom, validTo, enabled);
                savedId = Convert.ToInt64(await insertCommand.ExecuteScalarAsync(cancellationToken));
            }

            await transaction.CommitAsync(cancellationToken);

            var list = await GetGroupListAsync(cancellationToken: cancellationToken);
            return list.First(x => x.IdPromoGroup == savedId);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<ProductPromoGroupReconcileResultDto> ReconcileGroupAsync(
        long idPromoGroup,
        int? maxArticles = null,
        CancellationToken cancellationToken = default)
    {
        if (idPromoGroup <= 0)
            throw new ArgumentOutOfRangeException(nameof(idPromoGroup));

        if (maxArticles.HasValue && maxArticles.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxArticles));

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            const string groupSql = """
                SELECT TOP (1)
                    IdPromoGroup,
                    GroupType,
                    GroupId,
                    DiscountPercent,
                    ValidFrom,
                    ValidTo,
                    Enabled
                FROM dbo.Scan2EnterPromoGroups
                WHERE IdPromoGroup = @idPromoGroup;
                """;

            long groupId;
            decimal discountPercent;
            DateTime? validFrom;
            DateTime? validTo;
            bool enabled;

            await using (var groupCommand = new SqlCommand(groupSql, connection, (SqlTransaction)transaction))
            {
                groupCommand.Parameters.AddWithValue("@idPromoGroup", idPromoGroup);
                await using var reader = await groupCommand.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                    throw new InvalidOperationException("Promo di gruppo non trovata.");

                var groupType = reader.GetString(reader.GetOrdinal("GroupType"));
                if (!string.Equals(groupType, ProducerGroupType, StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException($"Tipo gruppo non supportato: {groupType}.");

                groupId = reader.GetInt64(reader.GetOrdinal("GroupId"));
                discountPercent = reader.GetDecimal(reader.GetOrdinal("DiscountPercent"));
                validFrom = reader.IsDBNull(reader.GetOrdinal("ValidFrom")) ? null : reader.GetDateTime(reader.GetOrdinal("ValidFrom"));
                validTo = reader.IsDBNull(reader.GetOrdinal("ValidTo")) ? null : reader.GetDateTime(reader.GetOrdinal("ValidTo"));
                enabled = reader.GetBoolean(reader.GetOrdinal("Enabled"));
            }

            var now = DateTime.Now;
            var activeNow = enabled &&
                (!validFrom.HasValue || now >= validFrom.Value) &&
                (!validTo.HasValue || now <= validTo.Value);

            if (!activeNow)
            {
                var removed = await RemoveGroupMaterializationAsync(connection, (SqlTransaction)transaction, idPromoGroup, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new ProductPromoGroupReconcileResultDto
                {
                    IdPromoGroup = idPromoGroup,
                    RequestedLimit = maxArticles,
                    ActiveNow = false,
                    Removed = removed
                };
            }

            var topClause = maxArticles.HasValue ? "TOP (@maxArticles)" : string.Empty;
            var candidateSql = $"""
                SELECT {topClause}
                    a.IdArticolo,
                    price.PublicPrice
                FROM dbo.tabArticoli AS a
                OUTER APPLY
                (
                    SELECT TOP (1)
                        CAST(pv.PrezzoVendita AS decimal(18,2)) AS PublicPrice
                    FROM dbo.tabPrezziVendita AS pv
                    WHERE pv.IdArticolo = a.IdArticolo
                      AND pv.IdListino = 1
                      AND ISNULL(pv.idVariante1, -1) = -1
                      AND ISNULL(pv.idVariante2, -1) = -1
                      AND ISNULL(pv.idVariante3, -1) = -1
                    ORDER BY pv.DataAgg DESC
                ) AS price
                WHERE a.IdProduttore = @groupId
                  AND ISNULL(a.Attivo, 0) = 1
                  AND price.PublicPrice > 0
                ORDER BY a.IdArticolo;
                """;

            var candidates = new List<(long ArticleId, decimal PublicPrice)>();
            await using (var candidateCommand = new SqlCommand(candidateSql, connection, (SqlTransaction)transaction))
            {
                candidateCommand.Parameters.AddWithValue("@groupId", groupId);
                if (maxArticles.HasValue)
                    candidateCommand.Parameters.AddWithValue("@maxArticles", maxArticles.Value);

                await using var reader = await candidateCommand.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    candidates.Add((
                        Convert.ToInt64(reader.GetValue(reader.GetOrdinal("IdArticolo"))),
                        reader.GetDecimal(reader.GetOrdinal("PublicPrice"))));
                }
            }

            var activeIndividualArticles = new HashSet<long>();
            const string individualSql = """
                SELECT ArticleId
                FROM dbo.Scan2EnterPromoDiscounts
                WHERE (ValidFrom IS NULL OR ValidFrom <= @now)
                  AND (ValidTo IS NULL OR ValidTo >= @now);
                """;

            await using (var individualCommand = new SqlCommand(individualSql, connection, (SqlTransaction)transaction))
            {
                individualCommand.Parameters.AddWithValue("@now", now);
                await using var reader = await individualCommand.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    activeIndividualArticles.Add(reader.GetInt64(0));
            }

            var dueRows = new Dictionary<long, (string PrimaryHash, decimal? OfferPrice)>();
            const string dueSql = """
                SELECT idArticolo, primaryHash, PrezzoOfferta
                FROM due_prm.tabDettaglioPromoArticoliPrezzoImposto
                WHERE IdPromo = @promoId
                  AND ISNULL(idVariante1, -1) = -1
                  AND ISNULL(idVariante2, -1) = -1
                  AND ISNULL(idVariante3, -1) = -1;
                """;

            await using (var dueCommand = new SqlCommand(dueSql, connection, (SqlTransaction)transaction))
            {
                dueCommand.Parameters.AddWithValue("@promoId", Scan2EnterPromoId);
                await using var reader = await dueCommand.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var articleId = Convert.ToInt64(reader.GetValue(0));
                    var hash = reader.IsDBNull(1) ? null : reader.GetString(1);
                    var dueOfferPrice = reader.IsDBNull(2)
                        ? (decimal?)null
                        : Convert.ToDecimal(reader.GetValue(2));
                    if (!string.IsNullOrWhiteSpace(hash))
                        dueRows[articleId] = (hash, dueOfferPrice);
                }
            }

            var groupItems = new Dictionary<long, (bool Materialized, string? DuePrimaryHash, decimal PublicPrice, decimal OfferPrice)>();
            const string itemSql = """
                SELECT ArticleId, Materialized, DuePrimaryHash, PublicPrice, OfferPrice
                FROM dbo.Scan2EnterPromoGroupItems
                WHERE IdPromoGroup = @idPromoGroup;
                """;

            await using (var itemCommand = new SqlCommand(itemSql, connection, (SqlTransaction)transaction))
            {
                itemCommand.Parameters.AddWithValue("@idPromoGroup", idPromoGroup);
                await using var reader = await itemCommand.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    groupItems[reader.GetInt64(reader.GetOrdinal("ArticleId"))] = (
                        reader.GetBoolean(reader.GetOrdinal("Materialized")),
                        reader.IsDBNull(reader.GetOrdinal("DuePrimaryHash")) ? null : reader.GetString(reader.GetOrdinal("DuePrimaryHash")),
                        reader.GetDecimal(reader.GetOrdinal("PublicPrice")),
                        reader.GetDecimal(reader.GetOrdinal("OfferPrice")));
                }
            }

            var inserted = 0;
            var updated = 0;
            var skippedIndividual = 0;
            var conflicts = 0;

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var offerPrice = RoundToCommercialTenCents(
                    candidate.PublicPrice * (1m - discountPercent / 100m));

                if (activeIndividualArticles.Contains(candidate.ArticleId))
                {
                    skippedIndividual++;

                    // La promo individuale ha sempre precedenza sul gruppo.
                    // Se l'articolo era materializzato dal gruppo, liberiamo solo
                    // il tracking Scan2Enter: NON cancelliamo la riga Due, perché
                    // la stessa riga/hash può essere già stata presa in carico
                    // dalla promo individuale.
                    if (groupItems.TryGetValue(candidate.ArticleId, out var individualTracked) &&
                        individualTracked.Materialized)
                    {
                        await UpsertGroupItemAsync(
                            connection,
                            (SqlTransaction)transaction,
                            idPromoGroup,
                            candidate.ArticleId,
                            candidate.PublicPrice,
                            offerPrice,
                            duePrimaryHash: null,
                            materialized: false,
                            cancellationToken);

                        groupItems[candidate.ArticleId] =
                            (false, null, candidate.PublicPrice, offerPrice);
                    }

                    continue;
                }

                groupItems.TryGetValue(candidate.ArticleId, out var tracked);
                var hasExistingDueRow =
                    dueRows.TryGetValue(candidate.ArticleId, out var existingDueRow);
                var existingDueHash =
                    hasExistingDueRow ? existingDueRow.PrimaryHash : null;

                if (tracked.Materialized &&
                    !string.IsNullOrWhiteSpace(tracked.DuePrimaryHash) &&
                    string.Equals(tracked.DuePrimaryHash, existingDueHash, StringComparison.Ordinal))
                {
                    // La materializzazione e' gia' corretta solo se coincidono
                    // sia il tracking Scan2Enter sia il PrezzoOfferta reale in Due.
                    var publicPriceChanged =
                        Math.Abs(tracked.PublicPrice - candidate.PublicPrice) > 0.0001m;
                    var trackedOfferPriceChanged =
                        Math.Abs(tracked.OfferPrice - offerPrice) > 0.0001m;
                    var dueOfferPriceChanged =
                        !existingDueRow.OfferPrice.HasValue ||
                        Math.Abs(existingDueRow.OfferPrice.Value - offerPrice) > 0.0001m;

                    if (!publicPriceChanged &&
                        !trackedOfferPriceChanged &&
                        !dueOfferPriceChanged)
                    {
                        continue;
                    }

                    const string updateDueSql = """
                        UPDATE due_prm.tabDettaglioPromoArticoliPrezzoImposto
                        SET PrezzoOfferta = @offerPrice,
                            DataAgg = GETDATE()
                        WHERE IdPromo = @promoId
                          AND primaryHash = @primaryHash
                          AND idArticolo = @articleId
                          AND (
                                PrezzoOfferta IS NULL
                                OR ABS(PrezzoOfferta - @offerPrice) > 0.0001
                              );
                        """;

                    await using var updateDueCommand = new SqlCommand(updateDueSql, connection, (SqlTransaction)transaction);
                    updateDueCommand.Parameters.AddWithValue("@offerPrice", offerPrice);
                    updateDueCommand.Parameters.AddWithValue("@promoId", Scan2EnterPromoId);
                    updateDueCommand.Parameters.AddWithValue("@primaryHash", tracked.DuePrimaryHash);
                    updateDueCommand.Parameters.AddWithValue("@articleId", candidate.ArticleId);
                    updated += await updateDueCommand.ExecuteNonQueryAsync(cancellationToken);

                    await UpsertGroupItemAsync(connection, (SqlTransaction)transaction, idPromoGroup, candidate.ArticleId,
                        candidate.PublicPrice, offerPrice, tracked.DuePrimaryHash, true, cancellationToken);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(existingDueHash))
                {
                    conflicts++;
                    continue;
                }

                var newDetailId = await GetNewDetailIdAsync(connection, (SqlTransaction)transaction, cancellationToken);
                var primaryHash = newDetailId.ToString(System.Globalization.CultureInfo.InvariantCulture);

                const string insertDueSql = """
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

                await using var insertDueCommand = new SqlCommand(insertDueSql, connection, (SqlTransaction)transaction);
                insertDueCommand.Parameters.AddWithValue("@promoId", Scan2EnterPromoId);
                insertDueCommand.Parameters.AddWithValue("@offerPrice", offerPrice);
                insertDueCommand.Parameters.AddWithValue("@primaryHash", primaryHash);
                insertDueCommand.Parameters.AddWithValue("@articleId", candidate.ArticleId);
                inserted += await insertDueCommand.ExecuteNonQueryAsync(cancellationToken);

                await UpsertGroupItemAsync(connection, (SqlTransaction)transaction, idPromoGroup, candidate.ArticleId,
                    candidate.PublicPrice, offerPrice, primaryHash, true, cancellationToken);

                dueRows[candidate.ArticleId] = (primaryHash, offerPrice);
                groupItems[candidate.ArticleId] = (true, primaryHash, candidate.PublicPrice, offerPrice);
            }

            await transaction.CommitAsync(cancellationToken);

            return new ProductPromoGroupReconcileResultDto
            {
                IdPromoGroup = idPromoGroup,
                RequestedLimit = maxArticles,
                ActiveNow = true,
                Candidates = candidates.Count,
                Inserted = inserted,
                Updated = updated,
                SkippedIndividual = skippedIndividual,
                Conflicts = conflicts
            };
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<bool> DeleteGroupAsync(
        long idPromoGroup,
        CancellationToken cancellationToken = default)
    {
        if (idPromoGroup <= 0)
            return false;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await RemoveGroupMaterializationAsync(connection, (SqlTransaction)transaction, idPromoGroup, cancellationToken);

            const string deleteSql = """
                DELETE FROM dbo.Scan2EnterPromoGroups
                WHERE IdPromoGroup = @idPromoGroup;
                SELECT @@ROWCOUNT;
                """;

            await using var command = new SqlCommand(deleteSql, connection, (SqlTransaction)transaction);
            command.Parameters.AddWithValue("@idPromoGroup", idPromoGroup);
            var affected = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));

            await transaction.CommitAsync(cancellationToken);
            return affected > 0;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
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

    private static void AddGroupParameters(
        SqlCommand command,
        long producerId,
        string? producerCode,
        string producerDescription,
        decimal discountPercent,
        DateTime? validFrom,
        DateTime? validTo,
        bool enabled)
    {
        command.Parameters.AddWithValue("@groupType", ProducerGroupType);
        command.Parameters.AddWithValue("@groupId", producerId);
        command.Parameters.Add("@groupCode", System.Data.SqlDbType.NVarChar, 50).Value =
            string.IsNullOrWhiteSpace(producerCode) ? DBNull.Value : producerCode;
        command.Parameters.AddWithValue("@groupDescription", producerDescription);
        command.Parameters.AddWithValue("@discountPercent", discountPercent);
        command.Parameters.Add("@validFrom", System.Data.SqlDbType.DateTime).Value =
            validFrom.HasValue ? validFrom.Value : DBNull.Value;
        command.Parameters.Add("@validTo", System.Data.SqlDbType.DateTime).Value =
            validTo.HasValue ? validTo.Value : DBNull.Value;
        command.Parameters.AddWithValue("@enabled", enabled);
    }

    private static async Task UpsertGroupItemAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long idPromoGroup,
        long articleId,
        decimal publicPrice,
        decimal offerPrice,
        string? duePrimaryHash,
        bool materialized,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.Scan2EnterPromoGroupItems
            SET
                PublicPrice = @publicPrice,
                OfferPrice = @offerPrice,
                DuePrimaryHash = @duePrimaryHash,
                Materialized = @materialized,
                UpdatedAt = GETDATE()
            WHERE IdPromoGroup = @idPromoGroup
              AND ArticleId = @articleId;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO dbo.Scan2EnterPromoGroupItems
                (
                    IdPromoGroup,
                    ArticleId,
                    PublicPrice,
                    OfferPrice,
                    DuePrimaryHash,
                    Materialized,
                    CreatedAt,
                    UpdatedAt
                )
                VALUES
                (
                    @idPromoGroup,
                    @articleId,
                    @publicPrice,
                    @offerPrice,
                    @duePrimaryHash,
                    @materialized,
                    GETDATE(),
                    GETDATE()
                );
            END;
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@idPromoGroup", idPromoGroup);
        command.Parameters.AddWithValue("@articleId", articleId);
        command.Parameters.AddWithValue("@publicPrice", publicPrice);
        command.Parameters.AddWithValue("@offerPrice", offerPrice);
        command.Parameters.Add("@duePrimaryHash", System.Data.SqlDbType.NVarChar, 50).Value =
            string.IsNullOrWhiteSpace(duePrimaryHash) ? DBNull.Value : duePrimaryHash;
        command.Parameters.AddWithValue("@materialized", materialized);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> RemoveGroupMaterializationAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long idPromoGroup,
        CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE p
            FROM due_prm.tabDettaglioPromoArticoliPrezzoImposto AS p
            INNER JOIN dbo.Scan2EnterPromoGroupItems AS gi
                ON gi.DuePrimaryHash = p.primaryHash
               AND gi.ArticleId = p.idArticolo
            WHERE gi.IdPromoGroup = @idPromoGroup
              AND gi.Materialized = 1
              AND p.IdPromo = @promoId;

            DECLARE @removed int = @@ROWCOUNT;

            UPDATE dbo.Scan2EnterPromoGroupItems
            SET
                Materialized = 0,
                DuePrimaryHash = NULL,
                UpdatedAt = GETDATE()
            WHERE IdPromoGroup = @idPromoGroup
              AND Materialized = 1;

            SELECT @removed;
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@idPromoGroup", idPromoGroup);
        command.Parameters.AddWithValue("@promoId", Scan2EnterPromoId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
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


public sealed class ProductPromoProducerOptionDto
{
    public long ProducerId { get; set; }
    public string? ProducerCode { get; set; }
    public string ProducerDescription { get; set; } = "";
    public int EligibleArticles { get; set; }
}

public sealed class ProductPromoGroupDto
{
    public long IdPromoGroup { get; set; }
    public string GroupType { get; set; } = "";
    public long GroupId { get; set; }
    public string? GroupCode { get; set; }
    public string GroupDescription { get; set; } = "";
    public decimal DiscountPercent { get; set; }
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
    public bool Enabled { get; set; }
    public string Status { get; set; } = "";
    public int EligibleArticles { get; set; }
    public int MaterializedArticles { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class ProductPromoGroupReconcileResultDto
{
    public long IdPromoGroup { get; set; }
    public int? RequestedLimit { get; set; }
    public bool ActiveNow { get; set; }
    public int Candidates { get; set; }
    public int Inserted { get; set; }
    public int Updated { get; set; }
    public int Removed { get; set; }
    public int SkippedIndividual { get; set; }
    public int Conflicts { get; set; }
}

