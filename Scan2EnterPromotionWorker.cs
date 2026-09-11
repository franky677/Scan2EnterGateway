using Scan2EnterGateway.Data;

namespace Scan2EnterGateway;

/// <summary>
/// Worker del motore promozioni proprietario Scan2Enter.
/// Non usa le tabelle promo Due.
///
/// Riconcilia in due fasi:
/// 1) RESTORE delle promo non più attive;
/// 2) APPLY delle promo attive dal livello più debole al più forte,
///    così la precedenza finale è ARTICLE > BRAND, poi Priority più alta,
///    poi IdPromotion più basso.
///
/// Per BRAND materializza gli articoli tramite tabArticoli.IdProduttore.
/// Gli item BRAND non più eleggibili vengono ripristinati automaticamente.
/// </summary>
public sealed class Scan2EnterPromotionWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly Scan2EnterPromotionRepository _repository;
    private readonly ILogger<Scan2EnterPromotionWorker> _logger;

    public Scan2EnterPromotionWorker(
        Scan2EnterPromotionRepository repository,
        ILogger<Scan2EnterPromotionWorker> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ReconcileSafelyAsync(stoppingToken);

        using var timer = new PeriodicTimer(Interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await ReconcileSafelyAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Arresto normale del servizio.
        }
    }

    private async Task ReconcileSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var promotions = await _repository.GetAllAsync(cancellationToken);
            var now = DateTime.Now;

            // FASE 1: togliamo prima tutto ciò che non deve più essere attivo.
            foreach (var promotion in promotions)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var active =
                    promotion.IsEnabled &&
                    now >= promotion.StartDate &&
                    now <= promotion.EndDate;

                if (active)
                    continue;

                try
                {
                    await RestorePromotionAsync(promotion, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Errore RESTORE promozione Scan2Enter {IdPromotion} ({Name}).",
                        promotion.IdPromotion,
                        promotion.Name);
                }
            }

            // FASE 2: applichiamo dal più debole al più forte.
            // In questo modo l'ultimo APPLY lascia in vigore il vincitore:
            // ARTICLE > BRAND; Priority più alta; a parità IdPromotion più basso.
            var activePromotions = promotions
                .Where(p =>
                    p.IsEnabled &&
                    now >= p.StartDate &&
                    now <= p.EndDate)
                .OrderBy(p => TargetRank(p.TargetType))       // BRAND prima, ARTICLE dopo
                .ThenBy(p => p.Priority)                      // priorità bassa prima
                .ThenByDescending(p => p.IdPromotion)         // id alto prima, id basso dopo
                .ToList();

            foreach (var promotion in activePromotions)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await ApplyPromotionAsync(promotion, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Errore APPLY promozione Scan2Enter {IdPromotion} ({Name}).",
                        promotion.IdPromotion,
                        promotion.Name);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Errore durante la riconciliazione automatica delle promozioni proprietarie Scan2Enter.");
        }
    }

    private async Task RestorePromotionAsync(
        Scan2EnterPromotionDto promotion,
        CancellationToken cancellationToken)
    {
        var targetType = NormalizeTargetType(promotion.TargetType);

        if (targetType is not ("ARTICLE" or "BRAND"))
        {
            _logger.LogWarning(
                "Promozione Scan2Enter {IdPromotion} ({Name}) con TargetType non supportato: {TargetType}.",
                promotion.IdPromotion,
                promotion.Name,
                promotion.TargetType);
            return;
        }

        // Per il restore non serve ricalcolare gli articoli della marca:
        // ripristiniamo solo gli item realmente materializzati e ancora applicati.
        var items = await _repository.GetItemsAsync(
            promotion.IdPromotion,
            cancellationToken);

        foreach (var item in items.Where(x => x.IsApplied))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var restore = await _repository.RestoreArticleAsync(
                promotion.IdPromotion,
                item.IdArticolo,
                cancellationToken);

            if (restore.Conflict)
            {
                _logger.LogWarning(
                    "Promo Scan2Enter {IdPromotion}, articolo {ArticleId}: conflitto RESTORE: {Message}",
                    promotion.IdPromotion,
                    item.IdArticolo,
                    restore.Message);
            }
            else if (!restore.AlreadyRestored)
            {
                _logger.LogInformation(
                    "Promo Scan2Enter {IdPromotion}, articolo {ArticleId}: ripristinato AL PUBBLICO {BasePrice}. {Message}",
                    promotion.IdPromotion,
                    item.IdArticolo,
                    restore.CurrentPublicPrice,
                    restore.Message);
            }
        }
    }

    private async Task ApplyPromotionAsync(
        Scan2EnterPromotionDto promotion,
        CancellationToken cancellationToken)
    {
        var targetType = NormalizeTargetType(promotion.TargetType);

        if (!promotion.TargetId.HasValue || promotion.TargetId.Value <= 0)
        {
            _logger.LogWarning(
                "Promozione Scan2Enter {IdPromotion} ({Name}) {TargetType} senza TargetId valido.",
                promotion.IdPromotion,
                promotion.Name,
                targetType);
            return;
        }

        if (targetType == "ARTICLE")
        {
            await ApplyOneAsync(
                promotion,
                promotion.TargetId.Value,
                cancellationToken);
            return;
        }

        if (targetType == "BRAND")
        {
            var articleIds = await _repository.GetBrandArticleIdsAsync(
                promotion.TargetId.Value,
                cancellationToken);

            // Una BRAND può avere item materializzati in cicli precedenti che oggi
            // non appartengono più alla marca (cambio IdProduttore), sono diventati
            // inattivi oppure non hanno più il listino AL PUBBLICO.
            // Prima di applicare la BRAND corrente, ripristiniamo questi item stale.
            var eligibleArticleIds = articleIds.ToHashSet();

            var materializedItems = await _repository.GetItemsAsync(
                promotion.IdPromotion,
                cancellationToken);

            foreach (var staleItem in materializedItems.Where(
                         x => x.IsApplied && !eligibleArticleIds.Contains(x.IdArticolo)))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var restore = await _repository.RestoreArticleAsync(
                    promotion.IdPromotion,
                    staleItem.IdArticolo,
                    cancellationToken);

                if (restore.Conflict)
                {
                    _logger.LogWarning(
                        "Promo BRAND Scan2Enter {IdPromotion}, articolo stale {ArticleId}: conflitto RESTORE: {Message}",
                        promotion.IdPromotion,
                        staleItem.IdArticolo,
                        restore.Message);
                }
                else if (!restore.AlreadyRestored)
                {
                    _logger.LogInformation(
                        "Promo BRAND Scan2Enter {IdPromotion}, articolo stale {ArticleId}: rimosso dalla BRAND e ripristinato AL PUBBLICO {BasePrice}. {Message}",
                        promotion.IdPromotion,
                        staleItem.IdArticolo,
                        restore.CurrentPublicPrice,
                        restore.Message);
                }
            }

            if (articleIds.Count == 0)
            {
                _logger.LogWarning(
                    "Promozione BRAND Scan2Enter {IdPromotion} ({Name}): nessun articolo attivo con listino AL PUBBLICO per IdProduttore {IdProduttore}. Gli eventuali item stale sono stati ripristinati.",
                    promotion.IdPromotion,
                    promotion.Name,
                    promotion.TargetId.Value);
                return;
            }

            foreach (var articleId in articleIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ApplyOneAsync(
                    promotion,
                    articleId,
                    cancellationToken);
            }

            return;
        }

        _logger.LogWarning(
            "Promozione Scan2Enter {IdPromotion} ({Name}) con TargetType non supportato: {TargetType}.",
            promotion.IdPromotion,
            promotion.Name,
            promotion.TargetType);
    }

    private async Task ApplyOneAsync(
        Scan2EnterPromotionDto promotion,
        int articleId,
        CancellationToken cancellationToken)
    {
        var result = await _repository.ApplyArticleAsync(
            promotion.IdPromotion,
            articleId,
            cancellationToken);

        if (result.Conflict)
        {
            _logger.LogWarning(
                "Promo Scan2Enter {IdPromotion}, articolo {ArticleId}: conflitto APPLY: {Message}",
                promotion.IdPromotion,
                articleId,
                result.Message);
        }
        else if (!result.AlreadyApplied)
        {
            _logger.LogInformation(
                "Promo Scan2Enter {IdPromotion}, articolo {ArticleId}: {Message} Prezzo {PromoPrice}, base {BasePrice}.",
                promotion.IdPromotion,
                articleId,
                result.Message,
                result.PublishedPrice,
                result.OriginalPublicPrice);
        }
    }

    private static string NormalizeTargetType(string? value) =>
        (value ?? string.Empty).Trim().ToUpperInvariant();

    private static int TargetRank(string? targetType) =>
        NormalizeTargetType(targetType) switch
        {
            "ARTICLE" => 2,
            "BRAND" => 1,
            _ => 0
        };
}
