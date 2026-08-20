namespace ApiTransacciones.Persistence;

/// Mensaje del patrón Outbox: se persiste en la MISMA transacción que el Payment.
public class OutboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PaymentId { get; set; }
    public string Type { get; set; } = default!;
    public string Payload { get; set; } = "{}";
    public string Status { get; set; } = OutboxStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DispatchedAt { get; set; }
    public int RetryCount { get; set; }
}

public static class OutboxStatus
{
    public const string Pending    = "PENDING";
    public const string Dispatched = "DISPATCHED";
    public const string Failed     = "FAILED";
}
