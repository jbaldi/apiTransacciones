namespace ApiTransacciones.Domain;

/// Nombres estables de los eventos que van al EventLog inmutable.
public static class DomainEvents
{
    public const string PaymentReceived         = "PaymentReceived";
    public const string SentToProcessor         = "SentToProcessor";
    public const string ProcessorSucceeded      = "ProcessorSucceeded";
    public const string ProcessorFailed         = "ProcessorFailed";
    public const string ProcessorTimeout        = "ProcessorTimeout";
    public const string MarkedUncertain         = "MarkedUncertain";
    public const string RoutedToAlternative     = "RoutedToAlternative";
    public const string ReconciliationChecked   = "ReconciliationChecked";
    public const string ReconciliationConfirmed = "ReconciliationConfirmed";
    public const string StateChanged            = "StateChanged";
}
