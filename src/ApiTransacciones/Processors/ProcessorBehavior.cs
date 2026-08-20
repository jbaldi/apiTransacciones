namespace ApiTransacciones.Processors;

/// "Guion" configurable del procesador falso, para forzar escenarios en la demo.
/// mode: ok | fail | timeout | slow. failCount: cuántas veces falla/timeoutea antes de portarse bien.
public class ProcessorBehavior
{
    private readonly object _lock = new();
    public string Mode { get; private set; } = "ok";
    public int FailCount { get; private set; }
    public ProcessorStatus StatusResult { get; private set; } = ProcessorStatus.Paid;

    private int _remaining;

    public void Set(string mode, int failCount, ProcessorStatus statusResult = ProcessorStatus.Paid)
    {
        lock (_lock)
        {
            Mode = mode;
            FailCount = failCount;
            _remaining = failCount;
            StatusResult = statusResult;
        }
    }

    /// Consume un intento: devuelve true si este intento debe fallar/timeoutear según el guion.
    public bool ShouldMisbehaveOnce()
    {
        lock (_lock)
        {
            if (_remaining <= 0) return false;
            _remaining--;
            return true;
        }
    }
}
