using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace ApiTransacciones.Resilience;

/// Construye los pipelines de resiliencia UNA sola vez y los reutiliza (los pipelines de
/// Polly son thread-safe y están pensados para reusarse; además el CircuitBreakerStateProvider
/// sólo puede atarse a un pipeline, y el breaker necesita persistir su ventana entre cobros).
/// - Primary: timeout corto + reintentos con backoff exponencial + circuit breaker.
/// - Alternativo: timeout corto + reintentos, SIN breaker (es el plan B).
public class ResiliencePipelineFactory
{
    public CircuitBreakerStateProvider Breaker { get; } = new();
    public ResiliencePipeline Primary { get; }
    public ResiliencePipeline Alternative { get; }

    public ResiliencePipelineFactory()
    {
        // Pipeline del procesador primario. El breaker abre ante fallos repetidos.
        Primary = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential, // backoff exponencial
                Delay = TimeSpan.FromMilliseconds(200),
                UseJitter = true,
                ShouldHandle = new PredicateBuilder().Handle<Exception>()
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                MinimumThroughput = 2,
                SamplingDuration = TimeSpan.FromSeconds(10),
                BreakDuration = TimeSpan.FromSeconds(5),
                StateProvider = Breaker,
                ShouldHandle = new PredicateBuilder().Handle<Exception>()
            })
            .AddTimeout(TimeSpan.FromSeconds(2)) // timeout corto: no colgamos
            .Build();

        // Pipeline del procesador alternativo: timeout + reintentos, sin breaker.
        Alternative = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromMilliseconds(200),
                UseJitter = true,
                ShouldHandle = new PredicateBuilder().Handle<Exception>()
            })
            .AddTimeout(TimeSpan.FromSeconds(2))
            .Build();
    }
}
