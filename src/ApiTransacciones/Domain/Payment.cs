namespace ApiTransacciones.Domain;

/// Entidad raíz del pago. El cambio de estado pasa siempre por la máquina de estados.
public class Payment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string IdempotencyKey { get; set; } = default!;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "ARS";
    public string State { get; set; } = PaymentState.Pendiente;
    public string? ProcessorUsed { get; set; }
    public string? ProcessorRef { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? ResponseSnapshot { get; set; }

    /// Cambia de estado validando la transición. Devuelve el estado anterior.
    public string TransitionTo(string newState, TimeProvider clock)
    {
        PaymentStateMachine.EnsureTransition(State, newState);
        var previous = State;
        State = newState;
        UpdatedAt = clock.GetUtcNow();
        return previous;
    }
}
