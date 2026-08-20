using ApiTransacciones.Processors;

namespace ApiTransacciones.Api;

public record ProcessorBehaviorRequest(string Processor, string Mode, int FailCount, string? StatusResult);

public static class DemoEndpoints
{
    public static void MapDemo(this WebApplication app)
    {
        // Configura el guion del procesador falso para grabar cada escenario de la demo.
        app.MapPost("/demo/processor-behavior", (ProcessorBehaviorRequest body, ProcessorRegistry reg) =>
        {
            var status = body.StatusResult?.ToUpperInvariant() switch
            {
                "FAILED" => ProcessorStatus.Failed,
                "UNKNOWN" => ProcessorStatus.Unknown,
                _ => ProcessorStatus.Paid
            };
            reg.BehaviorFor(body.Processor).Set(body.Mode, body.FailCount, status);
            return Results.Ok(new { body.Processor, body.Mode, body.FailCount, status = status.ToString() });
        });
    }
}
