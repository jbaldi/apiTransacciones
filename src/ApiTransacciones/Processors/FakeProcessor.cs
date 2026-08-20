namespace ApiTransacciones.Processors;

/// Procesador de pago FALSO e in-memory. Simula latencia, fallos y timeouts según su guion.
public class FakeProcessor(string name, ProcessorBehavior behavior, TimeProvider clock) : IPaymentProcessor
{
    public string Name => name;

    public async Task<ChargeResult> ChargeAsync(string idempotencyKey, decimal amount, CancellationToken ct)
    {
        var misbehave = behavior.ShouldMisbehaveOnce();
        switch (behavior.Mode)
        {
            case "timeout" when misbehave:
                await Task.Delay(TimeSpan.FromSeconds(30), clock, ct); // será cortado por el timeout de Polly
                throw new TimeoutException();
            case "fail" when misbehave:
                return new ChargeResult(ChargeOutcome.Failed, null, "rechazado por el procesador");
            case "slow":
                await Task.Delay(TimeSpan.FromMilliseconds(200), clock, ct);
                break;
        }
        // Éxito: devolvemos una referencia de operación (clave para conciliar después).
        return new ChargeResult(ChargeOutcome.Ok, $"{name}-{idempotencyKey}", null);
    }

    // Fuente de verdad del dinero: el estado real de la operación según el procesador.
    public Task<ProcessorStatus> GetStatusAsync(string processorRef, CancellationToken ct)
        => Task.FromResult(behavior.StatusResult);
}

/// Registro de procesadores nombrados + sus guiones, para el router y el endpoint de demo.
public class ProcessorRegistry
{
    public required IPaymentProcessor Primary { get; init; }
    public required IPaymentProcessor Alternative { get; init; }
    public required ProcessorBehavior PrimaryBehavior { get; init; }
    public required ProcessorBehavior AlternativeBehavior { get; init; }

    public ProcessorBehavior BehaviorFor(string processor) =>
        processor.Equals("alternative", StringComparison.OrdinalIgnoreCase)
            ? AlternativeBehavior : PrimaryBehavior;
}
