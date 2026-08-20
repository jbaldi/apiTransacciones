namespace ApiTransacciones.Processors;

public enum ChargeOutcome { Ok, Failed, Timeout }
public record ChargeResult(ChargeOutcome Outcome, string? ProcessorRef, string? Reason);

public enum ProcessorStatus { Paid, Failed, Unknown }

/// Contrato del procesador externo (el "tercero"). Charge cobra; GetStatus es la fuente de verdad.
public interface IPaymentProcessor
{
    string Name { get; }
    Task<ChargeResult> ChargeAsync(string idempotencyKey, decimal amount, CancellationToken ct);
    Task<ProcessorStatus> GetStatusAsync(string processorRef, CancellationToken ct);
}
