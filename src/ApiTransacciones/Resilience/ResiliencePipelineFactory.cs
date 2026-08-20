using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace ApiTransacciones.Resilience;

/// Construye los pipelines de resiliencia del envío.
/// - Primary: timeout corto + reintentos con backoff exponencial + circuit breaker.
/// - Alternativo (OpenPass): timeout corto + reintentos, SIN breaker (es el plan B).
/// El breaker se expone para que el router decida el ruteo.
public class ResiliencePipelineFactory
{
    public CircuitBreakerStateProvider Breaker { get; } = new();

    // Pipeline del procesador primario. El breaker abre ante fallos repetidos.
    public ResiliencePipeline BuildPrimary() =>
        new ResiliencePipelineBuilder()
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
    public ResiliencePipeline BuildAlternative() =>
        new ResiliencePipelineBuilder()
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
