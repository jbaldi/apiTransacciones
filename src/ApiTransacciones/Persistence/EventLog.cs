using System.Text.Json;

namespace ApiTransacciones.Persistence;

/// Log de auditoría append-only. Sólo inserta; nunca actualiza ni borra.
public class EventLog(PaymentsDbContext db, TimeProvider clock)
{
    public async Task AppendAsync(Guid paymentId, string eventType, object? data = null, CancellationToken ct = default)
    {
        db.Events.Add(new EventLogEntry
        {
            PaymentId = paymentId,
            EventType = eventType,
            Data = data is null ? "{}" : JsonSerializer.Serialize(data),
            OccurredAt = clock.GetUtcNow()
        });
        await db.SaveChangesAsync(ct);
    }
}
