namespace ApiTransacciones.Domain;

/// Máquina de estados (Saga) del pago. Sólo permite transiciones válidas:
/// el dinero nunca "salta" a PAGADO sin pasar por el procesador.
public static class PaymentStateMachine
{
    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        [PaymentState.Pendiente] = [PaymentState.EnProceso],
        [PaymentState.EnProceso] = [PaymentState.Pagado, PaymentState.Fallido, PaymentState.Incierto],
        [PaymentState.Incierto]  = [PaymentState.Pagado, PaymentState.Fallido], // resuelto por conciliación
        [PaymentState.Pagado]    = [],  // estado terminal
        [PaymentState.Fallido]   = [],  // estado terminal
    };

    public static bool CanTransition(string from, string to)
        => Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static void EnsureTransition(string from, string to)
    {
        if (!CanTransition(from, to))
            throw new InvalidOperationException($"Transición inválida: {from} → {to}");
    }
}
