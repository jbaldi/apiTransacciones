using System.Text.Json;
using ApiTransacciones.Domain;
using ApiTransacciones.Persistence;
using ApiTransacciones.Processors;
using Microsoft.EntityFrameworkCore;

namespace ApiTransacciones.Workers;

/// MOMENTO 3 · INCERTIDUMBRE. Toma los pagos INCIERTO y le pregunta al procesador
/// por el estado real de la operación. El dinero lo define el procesador, no mi suposición.
public class ReconciliationWorker(IServiceProvider sp, ILogger<ReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ReconcileAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Error conciliando pagos INCIERTOS"); }
            await Task.Delay(TimeSpan.FromMilliseconds(200), stoppingToken);
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var reg = scope.ServiceProvider.GetRequiredService<ProcessorRegistry>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();

        var uncertain = await db.Payments
            .Where(p => p.State == PaymentState.Incierto)
            .Take(10).ToListAsync(ct);

        foreach (var payment in uncertain)
        {
            // Consultamos al MISMO procesador que usamos para cobrar.
            var processor = payment.ProcessorUsed == "alternative" ? reg.Alternative : reg.Primary;
            var status = await processor.GetStatusAsync(payment.ProcessorRef ?? "", ct);
            db.Events.Add(NewEvent(payment.Id, DomainEvents.ReconciliationChecked, clock,
                new { status = status.ToString() }));

            switch (status)
            {
                case ProcessorStatus.Paid:
                    payment.TransitionTo(PaymentState.Pagado, clock);
                    db.Events.Add(NewEvent(payment.Id, DomainEvents.ReconciliationConfirmed, clock,
                        new { estado = "PAGADO" }));
                    break;
                case ProcessorStatus.Failed:
                    payment.TransitionTo(PaymentState.Fallido, clock); // compensación (Saga)
                    db.Events.Add(NewEvent(payment.Id, DomainEvents.ReconciliationConfirmed, clock,
                        new { estado = "FALLIDO" }));
                    break;
                case ProcessorStatus.Unknown:
                    // Seguimos sin saber: NO tocamos el estado. Reintentamos en el próximo ciclo.
                    break;
            }
            await db.SaveChangesAsync(ct);
        }
    }

    private static EventLogEntry NewEvent(Guid paymentId, string type, TimeProvider clock, object? data = null) => new()
    {
        PaymentId = paymentId,
        EventType = type,
        Data = data is null ? "{}" : JsonSerializer.Serialize(data),
        OccurredAt = clock.GetUtcNow()
    };
}
