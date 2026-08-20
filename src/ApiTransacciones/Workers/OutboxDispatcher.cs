using ApiTransacciones.Domain;
using ApiTransacciones.Persistence;
using ApiTransacciones.Processors;
using ApiTransacciones.Resilience;
using Microsoft.EntityFrameworkCore;

namespace ApiTransacciones.Workers;

/// MOMENTO 2 · ENVÍO (asíncrono). La "cola": lee el Outbox y despacha el cobro.
/// Traduce el resultado del procesador a un estado del pago SIN asumir nada ante timeouts.
public class OutboxDispatcher(IServiceProvider sp, ILogger<OutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessBatchAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Error despachando el Outbox"); }
            await Task.Delay(TimeSpan.FromMilliseconds(200), stoppingToken);
        }
    }

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var router = scope.ServiceProvider.GetRequiredService<ProcessorRouter>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();

        var pending = await db.Outbox
            .Where(o => o.Status == OutboxStatus.Pending)
            .OrderBy(o => o.CreatedAt).Take(10).ToListAsync(ct);

        foreach (var msg in pending)
        {
            var payment = await db.Payments.FirstAsync(p => p.Id == msg.PaymentId, ct);
            payment.TransitionTo(PaymentState.EnProceso, clock);
            payment.Attempts++;
            db.Events.Add(NewEvent(payment.Id, DomainEvents.SentToProcessor, clock));

            // Reenviamos la MISMA idempotency-key al procesador (idempotencia end-to-end).
            var (result, processorUsed) = await router.ChargeAsync(payment.IdempotencyKey, payment.Amount, ct);
            payment.ProcessorUsed = processorUsed;

            switch (result.Outcome)
            {
                case ChargeOutcome.Ok:
                    payment.ProcessorRef = result.ProcessorRef;
                    payment.TransitionTo(PaymentState.Pagado, clock);
                    db.Events.Add(NewEvent(payment.Id, DomainEvents.ProcessorSucceeded, clock));
                    break;

                case ChargeOutcome.Failed:
                    payment.TransitionTo(PaymentState.Fallido, clock);
                    db.Events.Add(NewEvent(payment.Id, DomainEvents.ProcessorFailed, clock));
                    break;

                case ChargeOutcome.Timeout:
                    // Un timeout NO significa que falló: significa que no sé. Estado INCIERTO.
                    payment.ProcessorRef = result.ProcessorRef ?? $"{processorUsed}-{payment.IdempotencyKey}";
                    payment.TransitionTo(PaymentState.Incierto, clock);
                    db.Events.Add(NewEvent(payment.Id, DomainEvents.MarkedUncertain, clock));
                    break;
            }

            msg.Status = OutboxStatus.Dispatched;
            msg.DispatchedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
    }

    private static EventLogEntry NewEvent(Guid paymentId, string type, TimeProvider clock) => new()
    {
        PaymentId = paymentId,
        EventType = type,
        Data = "{}",
        OccurredAt = clock.GetUtcNow()
    };
}
