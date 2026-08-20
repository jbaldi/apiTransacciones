namespace ApiTransacciones.Api;

public record CreatePaymentRequest(decimal Amount, string Currency, string? CustomerId);
public record PaymentAccepted(Guid PaymentId, string State, string Message);
public record PaymentView(Guid PaymentId, string State, string? ProcessorUsed, int Attempts);

/// Ítem de la lista para la consola web: vista enriquecida de un pago.
public record PaymentListItem(
    Guid Id, decimal Amount, string Currency, string State,
    string? ProcessorUsed, string? ProcessorRef, int Attempts,
    string IdempotencyKey, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
