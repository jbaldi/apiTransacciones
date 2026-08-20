namespace ApiTransacciones.Persistence;

/// Entrada del log de auditoría inmutable (append-only).
public class EventLogEntry
{
    public long Id { get; set; } // autoincremento → orden garantizado
    public Guid PaymentId { get; set; }
    public string EventType { get; set; } = default!;
    public string Data { get; set; } = "{}";
    public DateTimeOffset OccurredAt { get; set; }
}
