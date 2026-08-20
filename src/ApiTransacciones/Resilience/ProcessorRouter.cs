using ApiTransacciones.Processors;
using Polly;
using Polly.CircuitBreaker;

namespace ApiTransacciones.Resilience;

/// Rutea el cobro: usa el primary a través de su pipeline; si el breaker está abierto,
/// si el primary revienta, o si rechaza el pago, va al alternative (OpenPass).
/// Devuelve qué procesador cobró. Ante timeout NO asume: devuelve ChargeOutcome.Timeout.
public class ProcessorRouter(ProcessorRegistry reg, ResiliencePipelineFactory pipelines)
{
    public async Task<(ChargeResult Result, string ProcessorUsed)> ChargeAsync(
        string idempotencyKey, decimal amount, CancellationToken ct)
    {
        // Breaker abierto → no intentamos el primary, vamos directo al alternativo.
        if (pipelines.Breaker.CircuitState == CircuitState.Open)
            return (await ChargeAlternative(idempotencyKey, amount, ct), reg.Alternative.Name);

        var primary = await ChargeThrough(pipelines.BuildPrimary(), reg.Primary, idempotencyKey, amount, ct);

        // Éxito o timeout del primary → se resuelve con ese resultado.
        // Fallo "claro" (rechazo) → reenviamos la MISMA key al alternativo.
        if (primary.Outcome == ChargeOutcome.Failed)
            return (await ChargeAlternative(idempotencyKey, amount, ct), reg.Alternative.Name);

        return (primary, reg.Primary.Name);
    }

    private Task<ChargeResult> ChargeAlternative(string key, decimal amount, CancellationToken ct)
        => ChargeThrough(pipelines.BuildAlternative(), reg.Alternative, key, amount, ct);

    /// Ejecuta el cobro a través de un pipeline de Polly, traduciendo cualquier
    /// excepción (timeout tras reintentos, breaker abierto) a ChargeOutcome.Timeout.
    private static async Task<ChargeResult> ChargeThrough(
        ResiliencePipeline pipeline, IPaymentProcessor processor,
        string key, decimal amount, CancellationToken ct)
    {
        try
        {
            return await pipeline.ExecuteAsync(
                async token => await processor.ChargeAsync(key, amount, token), ct);
        }
        catch (Exception)
        {
            // Timeout, breaker abierto u otra falla tras agotar reintentos → NO asumimos.
            return new ChargeResult(ChargeOutcome.Timeout, $"{processor.Name}-{key}", "timeout tras reintentos");
        }
    }
}
