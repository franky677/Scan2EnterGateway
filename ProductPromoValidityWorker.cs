using Scan2EnterGateway.Data;

namespace Scan2EnterGateway;

public sealed class ProductPromoValidityWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly ProductPromoRepository _repository;
    private readonly ILogger<ProductPromoValidityWorker> _logger;

    public ProductPromoValidityWorker(
        ProductPromoRepository repository,
        ILogger<ProductPromoValidityWorker> logger)
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
            {
                await ReconcileSafelyAsync(stoppingToken);
            }
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
            var changed =
                await _repository.ReconcileValidityAsync(cancellationToken);

            if (changed > 0)
            {
                _logger.LogInformation(
                    "Riconciliazione promo Scan2Enter completata: {Changed} righe Due aggiornate.",
                    changed);
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
                "Errore durante la riconciliazione automatica delle promo Scan2Enter.");
        }
    }
}
