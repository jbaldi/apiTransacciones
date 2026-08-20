namespace ApiTransacciones.Api;

public record CreatePaymentRequest(decimal Amount, string Currency, string? CustomerId);
public record PaymentAccepted(Guid PaymentId, string State, string Message);
public record PaymentView(Guid PaymentId, string State, string? ProcessorUsed, int Attempts);
