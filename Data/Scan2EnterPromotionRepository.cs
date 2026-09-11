using Microsoft.Data.SqlClient;

namespace Scan2EnterGateway.Data;

public sealed class Scan2EnterPromotionRepository
{
    private readonly string _connectionString;

    public Scan2EnterPromotionRepository(IConfiguration configuration)
    {
        _connectionString =
            configuration.GetConnectionString("DueDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'DueDatabase' is missing.");
    }

    public async Task<IReadOnlyList<Scan2EnterPromotionDto>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                IdPromotion,
                Name,
                TargetType,
                TargetId,
                TargetCode,
                DiscountPercent,
                FixedPrice,
                StartDate,
                EndDate,
                IsEnabled,
                Priority,
                CreatedAt,
                UpdatedAt
            FROM dbo.Scan2EnterPromotions
            ORDER BY
                IsEnabled DESC,
                StartDate DESC,
                IdPromotion DESC;
            """;

        var result = new List<Scan2EnterPromotionDto>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadPromotion(reader));
        }

        return result;
    }

    public async Task<Scan2EnterPromotionDto?> GetByIdAsync(
        int idPromotion,
        CancellationToken cancellationToken = default)
    {
        if (idPromotion <= 0)
            return null;

        const string sql = """
            SELECT
                IdPromotion,
                Name,
                TargetType,
                TargetId,
                TargetCode,
                DiscountPercent,
                FixedPrice,
                StartDate,
                EndDate,
                IsEnabled,
                Priority,
                CreatedAt,
                UpdatedAt
            FROM dbo.Scan2EnterPromotions
            WHERE IdPromotion = @idPromotion;
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@idPromotion", idPromotion);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return ReadPromotion(reader);
    }

    public async Task<IReadOnlyList<Scan2EnterPromotionItemDto>> GetItemsAsync(
        int idPromotion,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                Id,
                IdPromotion,
                IdArticolo,
                OriginalPublicPrice,
                PromoPublicPrice,
                LastPublishedPrice,
                IsApplied,
                AppliedAt,
                RestoredAt
            FROM dbo.Scan2EnterPromotionItems
            WHERE IdPromotion = @idPromotion
            ORDER BY IdArticolo;
            """;

        var result = new List<Scan2EnterPromotionItemDto>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@idPromotion", idPromotion);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new Scan2EnterPromotionItemDto
            {
                Id = reader.GetInt64(reader.GetOrdinal("Id")),
                IdPromotion = reader.GetInt32(reader.GetOrdinal("IdPromotion")),
                IdArticolo = reader.GetInt32(reader.GetOrdinal("IdArticolo")),
                OriginalPublicPrice =
                    reader.GetDecimal(reader.GetOrdinal("OriginalPublicPrice")),
                PromoPublicPrice =
                    reader.GetDecimal(reader.GetOrdinal("PromoPublicPrice")),
                LastPublishedPrice =
                    reader.IsDBNull(reader.GetOrdinal("LastPublishedPrice"))
                        ? null
                        : reader.GetDecimal(reader.GetOrdinal("LastPublishedPrice")),
                IsApplied =
                    reader.GetBoolean(reader.GetOrdinal("IsApplied")),
                AppliedAt =
                    reader.IsDBNull(reader.GetOrdinal("AppliedAt"))
                        ? null
                        : reader.GetDateTime(reader.GetOrdinal("AppliedAt")),
                RestoredAt =
                    reader.IsDBNull(reader.GetOrdinal("RestoredAt"))
                        ? null
                        : reader.GetDateTime(reader.GetOrdinal("RestoredAt"))
            });
        }

        return result;
    }

    public async Task<Scan2EnterPromotionDto> CreateAsync(
        Scan2EnterPromotionCreateDto dto,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
            throw new ArgumentException("Il nome della promozione è obbligatorio.");

        var targetType = dto.TargetType.Trim().ToUpperInvariant();

        if (targetType is not ("ARTICLE" or "BRAND"))
            throw new ArgumentException(
                "TargetType deve essere ARTICLE oppure BRAND.");

        if (!dto.DiscountPercent.HasValue && !dto.FixedPrice.HasValue)
            throw new ArgumentException(
                "Specificare DiscountPercent oppure FixedPrice.");

        if (dto.DiscountPercent.HasValue && dto.FixedPrice.HasValue)
            throw new ArgumentException(
                "DiscountPercent e FixedPrice non possono essere valorizzati contemporaneamente.");

        if (dto.DiscountPercent is < 0m or > 100m)
            throw new ArgumentOutOfRangeException(
                nameof(dto.DiscountPercent),
                "Lo sconto deve essere compreso tra 0 e 100.");

        if (dto.FixedPrice < 0m)
            throw new ArgumentOutOfRangeException(
                nameof(dto.FixedPrice),
                "Il prezzo fisso non può essere negativo.");

        if (dto.EndDate < dto.StartDate)
            throw new ArgumentException(
                "La data di fine promo non può precedere la data di inizio.");

        const string sql = """
            INSERT INTO dbo.Scan2EnterPromotions
            (
                Name,
                TargetType,
                TargetId,
                TargetCode,
                DiscountPercent,
                FixedPrice,
                StartDate,
                EndDate,
                IsEnabled,
                Priority,
                CreatedAt,
                UpdatedAt
            )
            OUTPUT INSERTED.IdPromotion
            VALUES
            (
                @name,
                @targetType,
                @targetId,
                @targetCode,
                @discountPercent,
                @fixedPrice,
                @startDate,
                @endDate,
                @isEnabled,
                @priority,
                SYSDATETIME(),
                SYSDATETIME()
            );
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection);

        command.Parameters.AddWithValue("@name", dto.Name.Trim());
        command.Parameters.AddWithValue("@targetType", targetType);
        command.Parameters.AddWithValue(
            "@targetId",
            dto.TargetId.HasValue ? dto.TargetId.Value : DBNull.Value);
        command.Parameters.AddWithValue(
            "@targetCode",
            string.IsNullOrWhiteSpace(dto.TargetCode)
                ? DBNull.Value
                : dto.TargetCode.Trim());
        command.Parameters.AddWithValue(
            "@discountPercent",
            dto.DiscountPercent.HasValue
                ? dto.DiscountPercent.Value
                : DBNull.Value);
        command.Parameters.AddWithValue(
            "@fixedPrice",
            dto.FixedPrice.HasValue
                ? dto.FixedPrice.Value
                : DBNull.Value);
        command.Parameters.AddWithValue("@startDate", dto.StartDate);
        command.Parameters.AddWithValue("@endDate", dto.EndDate);
        command.Parameters.AddWithValue("@isEnabled", dto.IsEnabled);
        command.Parameters.AddWithValue("@priority", dto.Priority);

        var id =
            Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken));

        return (await GetByIdAsync(id, cancellationToken))!;
    }


    public async Task<Scan2EnterPromotionDto> UpdateAsync(
        int idPromotion,
        Scan2EnterPromotionCreateDto dto,
        CancellationToken cancellationToken = default)
    {
        if (idPromotion <= 0)
            throw new ArgumentOutOfRangeException(nameof(idPromotion));

        ValidatePromotion(dto);

        var existing = await GetByIdAsync(idPromotion, cancellationToken)
            ?? throw new InvalidOperationException("Promozione Scan2Enter non trovata.");

        var targetType = dto.TargetType.Trim().ToUpperInvariant();
        var items = await GetItemsAsync(idPromotion, cancellationToken);

        if (items.Count > 0 &&
            (!string.Equals(existing.TargetType, targetType, StringComparison.OrdinalIgnoreCase) ||
             existing.TargetId != dto.TargetId))
        {
            throw new InvalidOperationException(
                "Non è possibile cambiare il target di una promozione già materializzata.");
        }

        const string sql = """
            UPDATE dbo.Scan2EnterPromotions
            SET Name=@name, TargetType=@targetType, TargetId=@targetId,
                TargetCode=@targetCode, DiscountPercent=@discountPercent,
                FixedPrice=@fixedPrice, StartDate=@startDate, EndDate=@endDate,
                IsEnabled=@isEnabled, Priority=@priority, UpdatedAt=SYSDATETIME()
            WHERE IdPromotion=@idPromotion;
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@idPromotion", idPromotion);
        command.Parameters.AddWithValue("@name", dto.Name.Trim());
        command.Parameters.AddWithValue("@targetType", targetType);
        command.Parameters.AddWithValue("@targetId",
            dto.TargetId.HasValue ? dto.TargetId.Value : DBNull.Value);
        command.Parameters.AddWithValue("@targetCode",
            string.IsNullOrWhiteSpace(dto.TargetCode) ? DBNull.Value : dto.TargetCode.Trim());
        command.Parameters.AddWithValue("@discountPercent",
            dto.DiscountPercent.HasValue ? dto.DiscountPercent.Value : DBNull.Value);
        command.Parameters.AddWithValue("@fixedPrice",
            dto.FixedPrice.HasValue ? dto.FixedPrice.Value : DBNull.Value);
        command.Parameters.AddWithValue("@startDate", dto.StartDate);
        command.Parameters.AddWithValue("@endDate", dto.EndDate);
        command.Parameters.AddWithValue("@isEnabled", dto.IsEnabled);
        command.Parameters.AddWithValue("@priority", dto.Priority);

        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Promozione Scan2Enter non trovata.");

        return (await GetByIdAsync(idPromotion, cancellationToken))!;
    }

    public async Task<Scan2EnterPromotionDto> SetEnabledAsync(
        int idPromotion,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        if (idPromotion <= 0)
            throw new ArgumentOutOfRangeException(nameof(idPromotion));

        const string sql = """
            UPDATE dbo.Scan2EnterPromotions
            SET IsEnabled=@isEnabled, UpdatedAt=SYSDATETIME()
            WHERE IdPromotion=@idPromotion;
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@idPromotion", idPromotion);
        command.Parameters.AddWithValue("@isEnabled", isEnabled);

        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Promozione Scan2Enter non trovata.");

        return (await GetByIdAsync(idPromotion, cancellationToken))!;
    }

    public async Task<Scan2EnterPromotionDeleteResultDto> DeleteSafelyAsync(
        int idPromotion,
        CancellationToken cancellationToken = default)
    {
        if (idPromotion <= 0)
            throw new ArgumentOutOfRangeException(nameof(idPromotion));

        var promotion = await GetByIdAsync(idPromotion, cancellationToken);
        if (promotion is null)
            return new Scan2EnterPromotionDeleteResultDto {
                IdPromotion=idPromotion, Deleted=false,
                Message="Promozione Scan2Enter non trovata."
            };

        // Blocca nuove applicazioni prima del ripristino.
        await SetEnabledAsync(idPromotion, false, cancellationToken);

        var items = await GetItemsAsync(idPromotion, cancellationToken);
        foreach (var item in items.Where(x => x.IsApplied))
        {
            var restored = await RestoreArticleAsync(
                idPromotion, item.IdArticolo, cancellationToken);

            if (!restored.Restored || restored.Conflict)
                return new Scan2EnterPromotionDeleteResultDto {
                    IdPromotion=idPromotion, Deleted=false, Conflict=true,
                    Message=$"Cancellazione bloccata: impossibile ripristinare in sicurezza l'articolo {item.IdArticolo}. {restored.Message}"
                };
        }

        var finalItems = await GetItemsAsync(idPromotion, cancellationToken);
        if (finalItems.Any(x => x.IsApplied))
            return new Scan2EnterPromotionDeleteResultDto {
                IdPromotion=idPromotion, Deleted=false, Conflict=true,
                Message="Cancellazione bloccata: esistono ancora articoli con promozione applicata."
            };

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            const string deleteItemsSql = """
                DELETE FROM dbo.Scan2EnterPromotionItems
                WHERE IdPromotion=@idPromotion;
                """;
            await using (var cmd = new SqlCommand(deleteItemsSql, connection, transaction))
            {
                cmd.Parameters.AddWithValue("@idPromotion", idPromotion);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            const string deletePromotionSql = """
                DELETE FROM dbo.Scan2EnterPromotions
                WHERE IdPromotion=@idPromotion;
                """;
            int affected;
            await using (var cmd = new SqlCommand(deletePromotionSql, connection, transaction))
            {
                cmd.Parameters.AddWithValue("@idPromotion", idPromotion);
                affected = await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            if (affected != 1)
                throw new InvalidOperationException("Promozione Scan2Enter non trovata durante la cancellazione.");

            await transaction.CommitAsync(cancellationToken);
            return new Scan2EnterPromotionDeleteResultDto {
                IdPromotion=idPromotion, Deleted=true,
                RestoredItems=finalItems.Count,
                Message="Promozione Scan2Enter cancellata in sicurezza."
            };
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static void ValidatePromotion(Scan2EnterPromotionCreateDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
            throw new ArgumentException("Il nome della promozione è obbligatorio.");

        var targetType = dto.TargetType.Trim().ToUpperInvariant();
        if (targetType is not ("ARTICLE" or "BRAND"))
            throw new ArgumentException("TargetType deve essere ARTICLE oppure BRAND.");

        if (!dto.DiscountPercent.HasValue && !dto.FixedPrice.HasValue)
            throw new ArgumentException("Specificare DiscountPercent oppure FixedPrice.");

        if (dto.DiscountPercent.HasValue && dto.FixedPrice.HasValue)
            throw new ArgumentException(
                "DiscountPercent e FixedPrice non possono essere valorizzati contemporaneamente.");

        if (dto.DiscountPercent is < 0m or > 100m)
            throw new ArgumentOutOfRangeException(
                nameof(dto.DiscountPercent), "Lo sconto deve essere compreso tra 0 e 100.");

        if (dto.FixedPrice < 0m)
            throw new ArgumentOutOfRangeException(
                nameof(dto.FixedPrice), "Il prezzo fisso non può essere negativo.");

        if (dto.EndDate < dto.StartDate)
            throw new ArgumentException("La data di fine promo non può precedere la data di inizio.");
    }


    public async Task<IReadOnlyList<int>> GetBrandArticleIdsAsync(
        int idProduttore,
        CancellationToken cancellationToken = default)
    {
        if (idProduttore <= 0)
            throw new ArgumentOutOfRangeException(nameof(idProduttore));

        const string sql = """
            SELECT a.idArticolo
            FROM dbo.tabArticoli AS a
            WHERE a.IdProduttore = @idProduttore
              AND ISNULL(a.Attivo, 1) = 1
              AND EXISTS
              (
                  SELECT 1
                  FROM dbo.tabPrezziVendita AS pv
                  WHERE pv.IdArticolo = a.idArticolo
                    AND pv.IdListino = 1
                    AND ISNULL(pv.idVariante1, -1) = -1
                    AND ISNULL(pv.idVariante2, -1) = -1
                    AND ISNULL(pv.idVariante3, -1) = -1
              )
            ORDER BY a.idArticolo;
            """;

        var result = new List<int>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@idProduttore", idProduttore);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(reader.GetInt32(0));

        return result;
    }


    public async Task<Scan2EnterPromotionPreviewDto> PreviewArticleAsync(
        int articleId,
        decimal? discountPercent,
        decimal? fixedPrice,
        CancellationToken cancellationToken = default)
    {
        if (articleId <= 0)
            throw new ArgumentOutOfRangeException(nameof(articleId));

        if (!discountPercent.HasValue && !fixedPrice.HasValue)
            throw new ArgumentException(
                "Specificare DiscountPercent oppure FixedPrice.");

        if (discountPercent.HasValue && fixedPrice.HasValue)
            throw new ArgumentException(
                "DiscountPercent e FixedPrice non possono essere valorizzati contemporaneamente.");

        if (discountPercent is < 0m or > 100m)
            throw new ArgumentOutOfRangeException(
                nameof(discountPercent),
                "Lo sconto deve essere compreso tra 0 e 100.");

        if (fixedPrice < 0m)
            throw new ArgumentOutOfRangeException(
                nameof(fixedPrice),
                "Il prezzo fisso non può essere negativo.");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT TOP (1)
                CAST(pv.PrezzoVendita AS decimal(18,2)) AS PublicPrice
            FROM dbo.tabPrezziVendita AS pv
            WHERE pv.IdArticolo = @articleId
              AND pv.IdListino = 1
              AND ISNULL(pv.idVariante1, -1) = -1
              AND ISNULL(pv.idVariante2, -1) = -1
              AND ISNULL(pv.idVariante3, -1) = -1
            ORDER BY pv.DataAgg DESC;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@articleId", articleId);

        var value = await command.ExecuteScalarAsync(cancellationToken);

        if (value is null || value == DBNull.Value)
        {
            throw new InvalidOperationException(
                "Prezzo AL PUBBLICO non trovato per l'articolo.");
        }

        var publicPrice = Convert.ToDecimal(value);

        decimal promoPrice;

        if (fixedPrice.HasValue)
        {
            promoPrice = fixedPrice.Value;
        }
        else
        {
            var mathematicalPrice =
                publicPrice *
                (1m - discountPercent!.Value / 100m);

            promoPrice =
                Math.Round(
                    mathematicalPrice * 10m,
                    0,
                    MidpointRounding.AwayFromZero) / 10m;
        }

        return new Scan2EnterPromotionPreviewDto
        {
            ArticleId = articleId,
            PublicPrice = publicPrice,
            DiscountPercent = discountPercent,
            FixedPrice = fixedPrice,
            PromoPrice = promoPrice
        };
    }


    public async Task<Scan2EnterPromotionApplyResultDto> ApplyArticleAsync(
        int idPromotion,
        int articleId,
        CancellationToken cancellationToken = default)
    {
        if (idPromotion <= 0) throw new ArgumentOutOfRangeException(nameof(idPromotion));
        if (articleId <= 0) throw new ArgumentOutOfRangeException(nameof(articleId));

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            const string promotionSql = """
                SELECT TargetType, TargetId, DiscountPercent, FixedPrice,
                       StartDate, EndDate, IsEnabled, Priority
                FROM dbo.Scan2EnterPromotions WITH (UPDLOCK, HOLDLOCK)
                WHERE IdPromotion = @idPromotion;
                """;

            string targetType;
            int? targetId;
            decimal? discountPercent;
            decimal? fixedPrice;
            DateTime startDate;
            DateTime endDate;
            bool isEnabled;
            int priority;

            await using (var cmd = new SqlCommand(promotionSql, connection, transaction))
            {
                cmd.Parameters.AddWithValue("@idPromotion", idPromotion);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                    throw new InvalidOperationException("Promozione Scan2Enter non trovata.");

                targetType = reader.GetString(reader.GetOrdinal("TargetType"));
                targetId = reader.IsDBNull(reader.GetOrdinal("TargetId"))
                    ? null : reader.GetInt32(reader.GetOrdinal("TargetId"));
                discountPercent = reader.IsDBNull(reader.GetOrdinal("DiscountPercent"))
                    ? null : reader.GetDecimal(reader.GetOrdinal("DiscountPercent"));
                fixedPrice = reader.IsDBNull(reader.GetOrdinal("FixedPrice"))
                    ? null : reader.GetDecimal(reader.GetOrdinal("FixedPrice"));
                startDate = reader.GetDateTime(reader.GetOrdinal("StartDate"));
                endDate = reader.GetDateTime(reader.GetOrdinal("EndDate"));
                isEnabled = reader.GetBoolean(reader.GetOrdinal("IsEnabled"));
                priority = reader.GetInt32(reader.GetOrdinal("Priority"));
            }

            if (!isEnabled)
                throw new InvalidOperationException("La promozione Scan2Enter non è abilitata.");
            var now = DateTime.Now;
            if (now < startDate || now > endDate)
                throw new InvalidOperationException("La promozione Scan2Enter non è attiva in questo momento.");
            var normalizedTargetType = targetType.Trim().ToUpperInvariant();
            if (normalizedTargetType is not ("ARTICLE" or "BRAND"))
                throw new InvalidOperationException("TargetType promozione non supportato.");

            if (!targetId.HasValue)
                throw new InvalidOperationException("La promozione non contiene un TargetId.");

            if (normalizedTargetType == "ARTICLE")
            {
                if (targetId.Value != articleId)
                    throw new InvalidOperationException(
                        "L'articolo non corrisponde al TargetId della promozione.");
            }
            else
            {
                const string brandMatchSql = """
                    SELECT COUNT_BIG(1)
                    FROM dbo.tabArticoli
                    WHERE idArticolo=@articleId
                      AND IdProduttore=@idProduttore;
                    """;

                await using var brandMatchCmd =
                    new SqlCommand(brandMatchSql, connection, transaction);
                brandMatchCmd.Parameters.AddWithValue("@articleId", articleId);
                brandMatchCmd.Parameters.AddWithValue("@idProduttore", targetId.Value);

                var brandMatch =
                    Convert.ToInt64(await brandMatchCmd.ExecuteScalarAsync(cancellationToken));

                if (brandMatch != 1)
                    throw new InvalidOperationException(
                        "L'articolo non appartiene al produttore della promozione BRAND.");
            }
            if (!discountPercent.HasValue && !fixedPrice.HasValue)
                throw new InvalidOperationException("La promozione non contiene un prezzo o uno sconto.");
            if (discountPercent.HasValue && fixedPrice.HasValue)
                throw new InvalidOperationException("DiscountPercent e FixedPrice sono entrambi valorizzati.");

            const string priceSql = """
                SELECT TOP (1) CAST(pv.PrezzoVendita AS decimal(18,4))
                FROM dbo.tabPrezziVendita AS pv WITH (UPDLOCK, HOLDLOCK)
                WHERE pv.IdArticolo = @articleId
                  AND pv.IdListino = 1
                  AND ISNULL(pv.idVariante1, -1) = -1
                  AND ISNULL(pv.idVariante2, -1) = -1
                  AND ISNULL(pv.idVariante3, -1) = -1
                ORDER BY pv.DataAgg DESC;
                """;

            decimal currentPrice;
            await using (var cmd = new SqlCommand(priceSql, connection, transaction))
            {
                cmd.Parameters.AddWithValue("@articleId", articleId);
                var value = await cmd.ExecuteScalarAsync(cancellationToken);
                if (value is null || value == DBNull.Value)
                    throw new InvalidOperationException("Prezzo AL PUBBLICO non trovato.");
                currentPrice = Convert.ToDecimal(value);
            }

            const string existingSql = """
                SELECT TOP (1) Id, OriginalPublicPrice, PromoPublicPrice,
                       LastPublishedPrice, IsApplied
                FROM dbo.Scan2EnterPromotionItems WITH (UPDLOCK, HOLDLOCK)
                WHERE IdPromotion = @idPromotion AND IdArticolo = @articleId;
                """;

            long? itemId = null;
            decimal? storedOriginal = null;
            decimal? lastPublished = null;
            bool isApplied = false;

            await using (var cmd = new SqlCommand(existingSql, connection, transaction))
            {
                cmd.Parameters.AddWithValue("@idPromotion", idPromotion);
                cmd.Parameters.AddWithValue("@articleId", articleId);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    itemId = reader.GetInt64(reader.GetOrdinal("Id"));
                    storedOriginal = reader.GetDecimal(reader.GetOrdinal("OriginalPublicPrice"));
                    lastPublished = reader.IsDBNull(reader.GetOrdinal("LastPublishedPrice"))
                        ? null : reader.GetDecimal(reader.GetOrdinal("LastPublishedPrice"));
                    isApplied = reader.GetBoolean(reader.GetOrdinal("IsApplied"));
                }
            }

            if (isApplied)
            {
                if (!lastPublished.HasValue)
                {
                    await InsertPromotionLogAsync(connection, transaction, idPromotion, articleId,
                        "PRICE_CHANGED", null, currentPrice,
                        "APPLY bloccato: manca LastPublishedPrice, impossibile distinguere una variazione esterna.",
                        cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return new Scan2EnterPromotionApplyResultDto {
                        IdPromotion=idPromotion, ArticleId=articleId,
                        OriginalPublicPrice=storedOriginal ?? currentPrice,
                        PromoPublicPrice=currentPrice,
                        PublishedPrice=currentPrice, Applied=false, Conflict=true,
                        Message="APPLY bloccato: manca l'ultimo prezzo pubblicato da Scan2Enter."
                    };
                }

                if (currentPrice != lastPublished.Value)
                {
                    // Il prezzo AL PUBBLICO è cambiato mentre la promo era attiva.
                    // Lo consideriamo il nuovo prezzo base, ricalcoliamo la promo
                    // (se percentuale) e ripubblichiamo il prezzo promozionale.
                    var newBasePrice = currentPrice;
                    var recalculatedPromoPrice = fixedPrice ??
                        Math.Round(newBasePrice * (1m - discountPercent!.Value / 100m) * 10m,
                            0, MidpointRounding.AwayFromZero) / 10m;

                    const string updateBaseSql = """
                        UPDATE dbo.Scan2EnterPromotionItems SET
                            OriginalPublicPrice=@newBase,
                            PromoPublicPrice=@promo
                        WHERE Id=@id;
                        """;
                    await using (var cmd = new SqlCommand(updateBaseSql, connection, transaction))
                    {
                        cmd.Parameters.AddWithValue("@newBase", newBasePrice);
                        cmd.Parameters.AddWithValue("@promo", recalculatedPromoPrice);
                        cmd.Parameters.AddWithValue("@id", itemId!.Value);
                        await cmd.ExecuteNonQueryAsync(cancellationToken);
                    }

                    const string republishSql = """
                        UPDATE dbo.tabPrezziVendita
                        SET PrezzoVendita=@promo, DataAgg=GETDATE()
                        WHERE IdArticolo=@articleId AND IdListino=1
                          AND ISNULL(idVariante1,-1)=-1
                          AND ISNULL(idVariante2,-1)=-1
                          AND ISNULL(idVariante3,-1)=-1;
                        """;
                    int republishAffected;
                    await using (var cmd = new SqlCommand(republishSql, connection, transaction))
                    {
                        cmd.Parameters.AddWithValue("@promo", recalculatedPromoPrice);
                        cmd.Parameters.AddWithValue("@articleId", articleId);
                        republishAffected = await cmd.ExecuteNonQueryAsync(cancellationToken);
                    }
                    if (republishAffected != 1)
                        throw new InvalidOperationException(
                            $"Ripubblicazione promo non sicura: righe modificate = {republishAffected}.");

                    const string markRepublishedSql = """
                        UPDATE dbo.Scan2EnterPromotionItems SET
                            LastPublishedPrice=@promo,
                            IsApplied=1,
                            AppliedAt=SYSDATETIME(),
                            RestoredAt=NULL
                        WHERE Id=@id;
                        """;
                    await using (var cmd = new SqlCommand(markRepublishedSql, connection, transaction))
                    {
                        cmd.Parameters.AddWithValue("@promo", recalculatedPromoPrice);
                        cmd.Parameters.AddWithValue("@id", itemId!.Value);
                        await cmd.ExecuteNonQueryAsync(cancellationToken);
                    }

                    await InsertPromotionLogAsync(connection, transaction, idPromotion, articleId,
                        "PRICE_CHANGED", lastPublished, newBasePrice,
                        "Rilevato nuovo prezzo base AL PUBBLICO durante promo; base aggiornata.",
                        cancellationToken);
                    await InsertPromotionLogAsync(connection, transaction, idPromotion, articleId,
                        "APPLY", newBasePrice, recalculatedPromoPrice,
                        "Promozione Scan2Enter ripubblicata sul nuovo prezzo base.",
                        cancellationToken);

                    await transaction.CommitAsync(cancellationToken);
                    return new Scan2EnterPromotionApplyResultDto {
                        IdPromotion=idPromotion, ArticleId=articleId,
                        OriginalPublicPrice=newBasePrice,
                        PromoPublicPrice=recalculatedPromoPrice,
                        PublishedPrice=recalculatedPromoPrice,
                        Applied=true, AlreadyApplied=false, Conflict=false,
                        Message="Nuovo prezzo base rilevato; promozione aggiornata e ripubblicata."
                    };
                }

                // Il prezzo pubblicato da Scan2Enter è ancora quello atteso,
                // ma la definizione della promo potrebbe essere stata modificata
                // (es. FixedPrice 22,50 -> 21,90 mentre la promo è attiva).
                // In quel caso ricalcoliamo sul prezzo base memorizzato e
                // ripubblichiamo immediatamente la nuova condizione.
                var basePrice = storedOriginal ?? currentPrice;
                var expectedPromoPrice = fixedPrice ??
                    Math.Round(basePrice * (1m - discountPercent!.Value / 100m) * 10m,
                        0, MidpointRounding.AwayFromZero) / 10m;

                if (expectedPromoPrice != lastPublished.Value)
                {
                    const string updatePromoDefinitionSql = """
                        UPDATE dbo.Scan2EnterPromotionItems SET
                            PromoPublicPrice=@promo
                        WHERE Id=@id;
                        """;
                    await using (var cmd = new SqlCommand(updatePromoDefinitionSql, connection, transaction))
                    {
                        cmd.Parameters.AddWithValue("@promo", expectedPromoPrice);
                        cmd.Parameters.AddWithValue("@id", itemId!.Value);
                        await cmd.ExecuteNonQueryAsync(cancellationToken);
                    }

                    const string republishChangedDefinitionSql = """
                        UPDATE dbo.tabPrezziVendita
                        SET PrezzoVendita=@promo, DataAgg=GETDATE()
                        WHERE IdArticolo=@articleId AND IdListino=1
                          AND ISNULL(idVariante1,-1)=-1
                          AND ISNULL(idVariante2,-1)=-1
                          AND ISNULL(idVariante3,-1)=-1;
                        """;
                    int republishAffected;
                    await using (var cmd = new SqlCommand(republishChangedDefinitionSql, connection, transaction))
                    {
                        cmd.Parameters.AddWithValue("@promo", expectedPromoPrice);
                        cmd.Parameters.AddWithValue("@articleId", articleId);
                        republishAffected = await cmd.ExecuteNonQueryAsync(cancellationToken);
                    }

                    if (republishAffected != 1)
                        throw new InvalidOperationException(
                            $"Ripubblicazione promo modificata non sicura: righe modificate = {republishAffected}.");

                    const string markChangedDefinitionSql = """
                        UPDATE dbo.Scan2EnterPromotionItems SET
                            PromoPublicPrice=@promo,
                            LastPublishedPrice=@promo,
                            IsApplied=1,
                            AppliedAt=SYSDATETIME(),
                            RestoredAt=NULL
                        WHERE Id=@id;
                        """;
                    await using (var cmd = new SqlCommand(markChangedDefinitionSql, connection, transaction))
                    {
                        cmd.Parameters.AddWithValue("@promo", expectedPromoPrice);
                        cmd.Parameters.AddWithValue("@id", itemId!.Value);
                        await cmd.ExecuteNonQueryAsync(cancellationToken);
                    }

                    await InsertPromotionLogAsync(connection, transaction, idPromotion, articleId,
                        "APPLY", lastPublished.Value, expectedPromoPrice,
                        "Definizione promozione modificata mentre attiva; nuovo prezzo promozionale ripubblicato.",
                        cancellationToken);

                    await transaction.CommitAsync(cancellationToken);
                    return new Scan2EnterPromotionApplyResultDto {
                        IdPromotion=idPromotion, ArticleId=articleId,
                        OriginalPublicPrice=basePrice,
                        PromoPublicPrice=expectedPromoPrice,
                        PublishedPrice=expectedPromoPrice,
                        Applied=true, AlreadyApplied=false, Conflict=false,
                        Message="Definizione promo modificata; nuovo prezzo promozionale ripubblicato."
                    };
                }

                await transaction.CommitAsync(cancellationToken);
                return new Scan2EnterPromotionApplyResultDto {
                    IdPromotion=idPromotion, ArticleId=articleId,
                    OriginalPublicPrice=basePrice,
                    PromoPublicPrice=lastPublished.Value,
                    PublishedPrice=currentPrice, Applied=true, AlreadyApplied=true,
                    Message="Promozione già applicata."
                };
            }

            // Precedenza proprietaria:
            // ARTICLE > BRAND; a parità di tipo vince Priority più alta;
            // a ulteriore parità vince l'IdPromotion più basso (più vecchia/stabile).
            const string overlapSql = """
                SELECT TOP (1)
                    i.Id,
                    i.IdPromotion,
                    i.OriginalPublicPrice,
                    p.TargetType,
                    p.Priority
                FROM dbo.Scan2EnterPromotionItems AS i WITH (UPDLOCK, HOLDLOCK)
                INNER JOIN dbo.Scan2EnterPromotions AS p WITH (UPDLOCK, HOLDLOCK)
                    ON p.IdPromotion = i.IdPromotion
                WHERE i.IdArticolo=@articleId
                  AND i.IsApplied=1
                  AND i.IdPromotion<>@idPromotion
                ORDER BY
                    CASE WHEN UPPER(p.TargetType)='ARTICLE' THEN 2 ELSE 1 END DESC,
                    p.Priority DESC,
                    p.IdPromotion ASC;
                """;

            long? overlapItemId = null;
            int? overlapId = null;
            decimal? overlapOriginal = null;
            string? overlapTargetType = null;
            int overlapPriority = 0;

            await using (var cmd = new SqlCommand(overlapSql, connection, transaction))
            {
                cmd.Parameters.AddWithValue("@articleId", articleId);
                cmd.Parameters.AddWithValue("@idPromotion", idPromotion);

                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    overlapItemId = reader.GetInt64(reader.GetOrdinal("Id"));
                    overlapId = reader.GetInt32(reader.GetOrdinal("IdPromotion"));
                    overlapOriginal =
                        reader.GetDecimal(reader.GetOrdinal("OriginalPublicPrice"));
                    overlapTargetType =
                        reader.GetString(reader.GetOrdinal("TargetType"));
                    overlapPriority =
                        reader.GetInt32(reader.GetOrdinal("Priority"));
                }
            }

            var currentRank = normalizedTargetType == "ARTICLE" ? 2 : 1;
            var currentWins = true;

            if (overlapId.HasValue)
            {
                var overlapRank =
                    string.Equals(overlapTargetType, "ARTICLE",
                        StringComparison.OrdinalIgnoreCase) ? 2 : 1;

                currentWins =
                    currentRank > overlapRank ||
                    (currentRank == overlapRank && priority > overlapPriority) ||
                    (currentRank == overlapRank && priority == overlapPriority &&
                     idPromotion < overlapId.Value);

                if (!currentWins)
                {
                    var suppressedBase = overlapOriginal ?? currentPrice;
                    var suppressedPromo = fixedPrice ??
                        Math.Round(
                            suppressedBase * (1m - discountPercent!.Value / 100m) * 10m,
                            0, MidpointRounding.AwayFromZero) / 10m;

                    await transaction.CommitAsync(cancellationToken);
                    return new Scan2EnterPromotionApplyResultDto
                    {
                        IdPromotion=idPromotion,
                        ArticleId=articleId,
                        OriginalPublicPrice=suppressedBase,
                        PromoPublicPrice=suppressedPromo,
                        PublishedPrice=currentPrice,
                        Applied=false,
                        AlreadyApplied=false,
                        Conflict=false,
                        Message=$"Promozione non applicata: precedenza alla promozione Scan2Enter {overlapId.Value}."
                    };
                }

                // La nuova promozione ha precedenza. Disattiviamo la precedente
                // SENZA ripristinare il prezzo: il vero prezzo base resta quello
                // memorizzato dalla promozione soppiantata e viene ereditato.
                const string supersedeSql = """
                    UPDATE dbo.Scan2EnterPromotionItems
                    SET IsApplied=0,
                        RestoredAt=SYSDATETIME()
                    WHERE Id=@id;
                    """;

                await using (var cmd = new SqlCommand(supersedeSql, connection, transaction))
                {
                    cmd.Parameters.AddWithValue("@id", overlapItemId!.Value);
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }

                await InsertPromotionLogAsync(
                    connection, transaction,
                    overlapId.Value, articleId,
                    "SUPERSEDED",
                    currentPrice, currentPrice,
                    $"Promozione soppiantata dalla Scan2Enter {idPromotion} senza ripristino intermedio del prezzo.",
                    cancellationToken);
            }

            var effectiveBasePrice =
                overlapOriginal ?? currentPrice;

            decimal promoPrice = fixedPrice ??
                Math.Round(
                    effectiveBasePrice * (1m - discountPercent!.Value / 100m) * 10m,
                    0, MidpointRounding.AwayFromZero) / 10m;

            if (itemId.HasValue)
            {
                const string sql = """
                    UPDATE dbo.Scan2EnterPromotionItems SET
                        OriginalPublicPrice=@original, PromoPublicPrice=@promo,
                        LastPublishedPrice=NULL, IsApplied=0,
                        AppliedAt=NULL, RestoredAt=NULL
                    WHERE Id=@id;
                    """;
                await using var cmd = new SqlCommand(sql, connection, transaction);
                cmd.Parameters.AddWithValue("@original", effectiveBasePrice);
                cmd.Parameters.AddWithValue("@promo", promoPrice);
                cmd.Parameters.AddWithValue("@id", itemId.Value);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            else
            {
                const string sql = """
                    INSERT INTO dbo.Scan2EnterPromotionItems
                    (IdPromotion,IdArticolo,OriginalPublicPrice,PromoPublicPrice,
                     LastPublishedPrice,IsApplied,AppliedAt,RestoredAt)
                    VALUES
                    (@idPromotion,@articleId,@original,@promo,NULL,0,NULL,NULL);
                    """;
                await using var cmd = new SqlCommand(sql, connection, transaction);
                cmd.Parameters.AddWithValue("@idPromotion", idPromotion);
                cmd.Parameters.AddWithValue("@articleId", articleId);
                cmd.Parameters.AddWithValue("@original", effectiveBasePrice);
                cmd.Parameters.AddWithValue("@promo", promoPrice);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            const string updatePriceSql = """
                UPDATE dbo.tabPrezziVendita
                SET PrezzoVendita=@promo, DataAgg=GETDATE()
                WHERE IdArticolo=@articleId AND IdListino=1
                  AND ISNULL(idVariante1,-1)=-1
                  AND ISNULL(idVariante2,-1)=-1
                  AND ISNULL(idVariante3,-1)=-1;
                """;
            int affected;
            await using (var cmd = new SqlCommand(updatePriceSql, connection, transaction))
            {
                cmd.Parameters.AddWithValue("@promo", promoPrice);
                cmd.Parameters.AddWithValue("@articleId", articleId);
                affected = await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            if (affected != 1)
                throw new InvalidOperationException(
                    $"Aggiornamento prezzo AL PUBBLICO non sicuro: righe modificate = {affected}.");

            const string markSql = """
                UPDATE dbo.Scan2EnterPromotionItems SET
                    PromoPublicPrice=@promo, LastPublishedPrice=@promo,
                    IsApplied=1, AppliedAt=SYSDATETIME(), RestoredAt=NULL
                WHERE IdPromotion=@idPromotion AND IdArticolo=@articleId;
                """;
            await using (var cmd = new SqlCommand(markSql, connection, transaction))
            {
                cmd.Parameters.AddWithValue("@promo", promoPrice);
                cmd.Parameters.AddWithValue("@idPromotion", idPromotion);
                cmd.Parameters.AddWithValue("@articleId", articleId);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertPromotionLogAsync(connection, transaction, idPromotion, articleId,
                "APPLY", effectiveBasePrice, promoPrice,
                "Prezzo AL PUBBLICO pubblicato da Scan2Enter.", cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new Scan2EnterPromotionApplyResultDto {
                IdPromotion=idPromotion, ArticleId=articleId,
                OriginalPublicPrice=effectiveBasePrice, PromoPublicPrice=promoPrice,
                PublishedPrice=promoPrice, Applied=true,
                Message="Promozione applicata al prezzo AL PUBBLICO."
            };
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<Scan2EnterPromotionRestoreResultDto> RestoreArticleAsync(
        int idPromotion, int articleId,
        CancellationToken cancellationToken = default)
    {
        if (idPromotion <= 0) throw new ArgumentOutOfRangeException(nameof(idPromotion));
        if (articleId <= 0) throw new ArgumentOutOfRangeException(nameof(articleId));

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            const string itemSql = """
                SELECT TOP (1) Id, OriginalPublicPrice, PromoPublicPrice,
                       LastPublishedPrice, IsApplied
                FROM dbo.Scan2EnterPromotionItems WITH (UPDLOCK, HOLDLOCK)
                WHERE IdPromotion=@idPromotion AND IdArticolo=@articleId;
                """;
            long itemId; decimal original; decimal promo; decimal? lastPublished; bool isApplied;
            await using (var cmd = new SqlCommand(itemSql, connection, transaction))
            {
                cmd.Parameters.AddWithValue("@idPromotion", idPromotion);
                cmd.Parameters.AddWithValue("@articleId", articleId);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                    throw new InvalidOperationException("Articolo promozione Scan2Enter non trovato.");
                itemId=reader.GetInt64(reader.GetOrdinal("Id"));
                original=reader.GetDecimal(reader.GetOrdinal("OriginalPublicPrice"));
                promo=reader.GetDecimal(reader.GetOrdinal("PromoPublicPrice"));
                lastPublished=reader.IsDBNull(reader.GetOrdinal("LastPublishedPrice"))
                    ? null : reader.GetDecimal(reader.GetOrdinal("LastPublishedPrice"));
                isApplied=reader.GetBoolean(reader.GetOrdinal("IsApplied"));
            }

            if (!isApplied)
            {
                await transaction.CommitAsync(cancellationToken);
                return new Scan2EnterPromotionRestoreResultDto {
                    IdPromotion=idPromotion, ArticleId=articleId,
                    OriginalPublicPrice=original, PromoPublicPrice=promo,
                    CurrentPublicPrice=original, Restored=true, AlreadyRestored=true,
                    Message="Promozione già ripristinata."
                };
            }

            const string priceSql = """
                SELECT TOP (1) CAST(PrezzoVendita AS decimal(18,4))
                FROM dbo.tabPrezziVendita WITH (UPDLOCK,HOLDLOCK)
                WHERE IdArticolo=@articleId AND IdListino=1
                  AND ISNULL(idVariante1,-1)=-1
                  AND ISNULL(idVariante2,-1)=-1
                  AND ISNULL(idVariante3,-1)=-1
                ORDER BY DataAgg DESC;
                """;
            decimal current;
            await using (var cmd = new SqlCommand(priceSql, connection, transaction))
            {
                cmd.Parameters.AddWithValue("@articleId", articleId);
                var value=await cmd.ExecuteScalarAsync(cancellationToken);
                if (value is null || value==DBNull.Value)
                    throw new InvalidOperationException("Prezzo AL PUBBLICO non trovato.");
                current=Convert.ToDecimal(value);
            }

            if (!lastPublished.HasValue)
            {
                await InsertPromotionLogAsync(connection, transaction, idPromotion, articleId,
                    "PRICE_CHANGED", null, current,
                    "RESTORE bloccato: manca LastPublishedPrice, impossibile determinare il prezzo base corretto.",
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new Scan2EnterPromotionRestoreResultDto {
                    IdPromotion=idPromotion, ArticleId=articleId,
                    OriginalPublicPrice=original, PromoPublicPrice=promo,
                    CurrentPublicPrice=current, Restored=false, Conflict=true,
                    Message="RESTORE bloccato: manca l'ultimo prezzo pubblicato da Scan2Enter."
                };
            }

            if (current != lastPublished.Value)
            {
                // Se al momento del RESTORE troviamo un prezzo esterno diverso
                // dall'ultimo pubblicato da Scan2Enter, quel prezzo è il nuovo
                // prezzo base. Non dobbiamo sovrascriverlo con il vecchio originale.
                const string acceptNewBaseSql = """
                    UPDATE dbo.Scan2EnterPromotionItems SET
                        OriginalPublicPrice=@newBase,
                        LastPublishedPrice=@newBase,
                        IsApplied=0,
                        RestoredAt=SYSDATETIME()
                    WHERE Id=@id;
                    """;
                await using (var cmd = new SqlCommand(acceptNewBaseSql, connection, transaction))
                {
                    cmd.Parameters.AddWithValue("@newBase", current);
                    cmd.Parameters.AddWithValue("@id", itemId);
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }

                await InsertPromotionLogAsync(connection, transaction, idPromotion, articleId,
                    "PRICE_CHANGED", lastPublished, current,
                    "RESTORE: rilevato nuovo prezzo base AL PUBBLICO; mantenuto senza sovrascriverlo.",
                    cancellationToken);
                await InsertPromotionLogAsync(connection, transaction, idPromotion, articleId,
                    "RESTORE", lastPublished, current,
                    "Promozione chiusa mantenendo il nuovo prezzo base esterno.",
                    cancellationToken);

                await transaction.CommitAsync(cancellationToken);
                return new Scan2EnterPromotionRestoreResultDto {
                    IdPromotion=idPromotion, ArticleId=articleId,
                    OriginalPublicPrice=current, PromoPublicPrice=promo,
                    CurrentPublicPrice=current, Restored=true, Conflict=false,
                    Message="Nuovo prezzo base rilevato e mantenuto; promozione ripristinata."
                };
            }

            const string restoreSql = """
                UPDATE dbo.tabPrezziVendita
                SET PrezzoVendita=@original, DataAgg=GETDATE()
                WHERE IdArticolo=@articleId AND IdListino=1
                  AND ISNULL(idVariante1,-1)=-1
                  AND ISNULL(idVariante2,-1)=-1
                  AND ISNULL(idVariante3,-1)=-1;
                """;
            int affected;
            await using (var cmd = new SqlCommand(restoreSql, connection, transaction))
            {
                cmd.Parameters.AddWithValue("@original", original);
                cmd.Parameters.AddWithValue("@articleId", articleId);
                affected=await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            if (affected != 1)
                throw new InvalidOperationException(
                    $"Ripristino prezzo AL PUBBLICO non sicuro: righe modificate = {affected}.");

            const string markSql = """
                UPDATE dbo.Scan2EnterPromotionItems SET
                    LastPublishedPrice=@original, IsApplied=0,
                    RestoredAt=SYSDATETIME()
                WHERE Id=@id;
                """;
            await using (var cmd = new SqlCommand(markSql, connection, transaction))
            {
                cmd.Parameters.AddWithValue("@original", original);
                cmd.Parameters.AddWithValue("@id", itemId);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertPromotionLogAsync(connection, transaction, idPromotion, articleId,
                "RESTORE", current, original,
                "Prezzo AL PUBBLICO originale ripristinato da Scan2Enter.", cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new Scan2EnterPromotionRestoreResultDto {
                IdPromotion=idPromotion, ArticleId=articleId,
                OriginalPublicPrice=original, PromoPublicPrice=promo,
                CurrentPublicPrice=original, Restored=true,
                Message="Prezzo AL PUBBLICO originale ripristinato."
            };
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task InsertPromotionLogAsync(
        SqlConnection connection, SqlTransaction transaction,
        int idPromotion, int articleId, string actionType,
        decimal? oldPrice, decimal? newPrice, string? message,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dbo.Scan2EnterPromotionLog
            (IdPromotion,IdArticolo,ActionType,OldPrice,NewPrice,Message,CreatedAt)
            VALUES
            (@idPromotion,@articleId,@actionType,@oldPrice,@newPrice,@message,SYSDATETIME());
            """;
        await using var cmd = new SqlCommand(sql, connection, transaction);
        cmd.Parameters.AddWithValue("@idPromotion", idPromotion);
        cmd.Parameters.AddWithValue("@articleId", articleId);
        cmd.Parameters.AddWithValue("@actionType", actionType);
        cmd.Parameters.AddWithValue("@oldPrice", oldPrice.HasValue ? oldPrice.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@newPrice", newPrice.HasValue ? newPrice.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@message",
            string.IsNullOrWhiteSpace(message) ? DBNull.Value : message);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static Scan2EnterPromotionDto ReadPromotion(SqlDataReader reader)
    {
        return new Scan2EnterPromotionDto
        {
            IdPromotion =
                reader.GetInt32(reader.GetOrdinal("IdPromotion")),
            Name =
                reader.GetString(reader.GetOrdinal("Name")),
            TargetType =
                reader.GetString(reader.GetOrdinal("TargetType")),
            TargetId =
                reader.IsDBNull(reader.GetOrdinal("TargetId"))
                    ? null
                    : reader.GetInt32(reader.GetOrdinal("TargetId")),
            TargetCode =
                reader.IsDBNull(reader.GetOrdinal("TargetCode"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("TargetCode")),
            DiscountPercent =
                reader.IsDBNull(reader.GetOrdinal("DiscountPercent"))
                    ? null
                    : reader.GetDecimal(reader.GetOrdinal("DiscountPercent")),
            FixedPrice =
                reader.IsDBNull(reader.GetOrdinal("FixedPrice"))
                    ? null
                    : reader.GetDecimal(reader.GetOrdinal("FixedPrice")),
            StartDate =
                reader.GetDateTime(reader.GetOrdinal("StartDate")),
            EndDate =
                reader.GetDateTime(reader.GetOrdinal("EndDate")),
            IsEnabled =
                reader.GetBoolean(reader.GetOrdinal("IsEnabled")),
            Priority =
                reader.GetInt32(reader.GetOrdinal("Priority")),
            CreatedAt =
                reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
            UpdatedAt =
                reader.GetDateTime(reader.GetOrdinal("UpdatedAt"))
        };
    }
}

public sealed class Scan2EnterPromotionDto
{
    public int IdPromotion { get; set; }
    public string Name { get; set; } = "";
    public string TargetType { get; set; } = "";
    public int? TargetId { get; set; }
    public string? TargetCode { get; set; }
    public decimal? DiscountPercent { get; set; }
    public decimal? FixedPrice { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public bool IsEnabled { get; set; }
    public int Priority { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class Scan2EnterPromotionCreateDto
{
    public string Name { get; set; } = "";
    public string TargetType { get; set; } = "";
    public int? TargetId { get; set; }
    public string? TargetCode { get; set; }
    public decimal? DiscountPercent { get; set; }
    public decimal? FixedPrice { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int Priority { get; set; }
}

public sealed class Scan2EnterPromotionItemDto
{
    public long Id { get; set; }
    public int IdPromotion { get; set; }
    public int IdArticolo { get; set; }
    public decimal OriginalPublicPrice { get; set; }
    public decimal PromoPublicPrice { get; set; }
    public decimal? LastPublishedPrice { get; set; }
    public bool IsApplied { get; set; }
    public DateTime? AppliedAt { get; set; }
    public DateTime? RestoredAt { get; set; }
}

public sealed class Scan2EnterPromotionDeleteResultDto
{
    public int IdPromotion { get; set; }
    public bool Deleted { get; set; }
    public int RestoredItems { get; set; }
    public bool Conflict { get; set; }
    public string Message { get; set; } = "";
}

public sealed class Scan2EnterPromotionPreviewDto
{
    public int ArticleId { get; set; }
    public decimal PublicPrice { get; set; }
    public decimal? DiscountPercent { get; set; }
    public decimal? FixedPrice { get; set; }
    public decimal PromoPrice { get; set; }
}

public sealed class Scan2EnterPromotionApplyResultDto
{
    public int IdPromotion { get; set; }
    public int ArticleId { get; set; }
    public decimal OriginalPublicPrice { get; set; }
    public decimal PromoPublicPrice { get; set; }
    public decimal PublishedPrice { get; set; }
    public bool Applied { get; set; }
    public bool AlreadyApplied { get; set; }
    public bool Conflict { get; set; }
    public string Message { get; set; } = "";
}

public sealed class Scan2EnterPromotionRestoreResultDto
{
    public int IdPromotion { get; set; }
    public int ArticleId { get; set; }
    public decimal OriginalPublicPrice { get; set; }
    public decimal PromoPublicPrice { get; set; }
    public decimal CurrentPublicPrice { get; set; }
    public bool Restored { get; set; }
    public bool AlreadyRestored { get; set; }
    public bool Conflict { get; set; }
    public string Message { get; set; } = "";
}
