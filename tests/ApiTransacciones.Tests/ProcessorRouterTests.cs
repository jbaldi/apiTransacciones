using ApiTransacciones.Processors;
using ApiTransacciones.Resilience;
using Polly.CircuitBreaker;
using Xunit;

public class ProcessorRouterTests
{
    // Procesador que siempre revienta (simula un tercero caído).
    private sealed class AlwaysThrows(string name) : IPaymentProcessor
    {
        public string Name => name;
        public Task<ChargeResult> ChargeAsync(string k, decimal a, CancellationToken ct)
            => throw new TimeoutException("caído");
        public Task<ProcessorStatus> GetStatusAsync(string r, CancellationToken ct)
            => Task.FromResult(ProcessorStatus.Unknown);
    }

    // Procesador que siempre cobra bien (el alternativo / OpenPass).
    private sealed class AlwaysOk(string name) : IPaymentProcessor
    {
        public string Name => name;
        public Task<ChargeResult> ChargeAsync(string k, decimal a, CancellationToken ct)
            => Task.FromResult(new ChargeResult(ChargeOutcome.Ok, $"{name}-{k}", null));
        public Task<ProcessorStatus> GetStatusAsync(string r, CancellationToken ct)
            => Task.FromResult(ProcessorStatus.Paid);
    }

    [Fact]
    public async Task Breaker_SeAbre_TrasFallosDelPrimary_YRuteaAlAlternativo()
    {
        var pipelines = new ResiliencePipelineFactory();
        var reg = new ProcessorRegistry
        {
            Primary = new AlwaysThrows("primary"),
            Alternative = new AlwaysOk("alternative"),
            PrimaryBehavior = new ProcessorBehavior(),
            AlternativeBehavior = new ProcessorBehavior()
        };
        var router = new ProcessorRouter(reg, pipelines);

        // Primer cobro: los reintentos hacen fallar el primary hasta ABRIR el breaker.
        var first = await router.ChargeAsync("key-1", 100m, default);
        Assert.Equal(ChargeOutcome.Timeout, first.Result.Outcome); // no asume: timeout
        Assert.Equal(CircuitState.Open, pipelines.Breaker.CircuitState);

        // Segundo cobro: con el breaker ABIERTO, se rutea directo al alternativo (OpenPass).
        var second = await router.ChargeAsync("key-2", 100m, default);
        Assert.Equal(ChargeOutcome.Ok, second.Result.Outcome);
        Assert.Equal("alternative", second.ProcessorUsed);
    }
}
