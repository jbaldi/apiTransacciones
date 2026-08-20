namespace ApiTransacciones.Domain;

/// Estados del ciclo de vida del pago. Valores string estables (persistidos y expuestos por API).
public static class PaymentState
{
    public const string Pendiente = "PENDIENTE";
    public const string EnProceso = "EN_PROCESO";
    public const string Pagado    = "PAGADO";
    public const string Fallido   = "FALLIDO";
    public const string Incierto  = "INCIERTO";
}
