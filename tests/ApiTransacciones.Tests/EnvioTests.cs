using System.Net.Http.Json;
using ApiTransacciones.Api;
using ApiTransacciones.Domain;
using ApiTransacciones.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public class EnvioTests
{
    private static async Task<Guid> CrearPago(HttpClient client, decimal amount = 1500m)
    {
        var r = new HttpRequestMessage(HttpMethod.Post, "/payments")
        { Content = JsonContent.Create(new CreatePaymentRequest(amount, "ARS", "cli")) };
        r.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var res = await client.SendAsync(r);
        return (await res.Content.ReadFromJsonAsync<PaymentAccepted>())!.PaymentId;
    }

    // Espera hasta que el pago llegue a un estado esperado o timeout (los workers son async).
    private static async Task<Payment> EsperarEstado(TestAppFactory app, Guid id, params string[] estados)
    {
        for (var i = 0; i < 80; i++)
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var p = await db.Payments.AsNoTracking().FirstAsync(x => x.Id == id);
            if (estados.Contains(p.State)) return p;
            await Task.Delay(100);
        }
        throw new Xunit.Sdk.XunitException($"El pago {id} no alcanzó {string.Join("/", estados)}");
    }

    [Fact]
    public async Task FalloClaroDelPrimary_RuteaAlAlternativo_YCobra()
    {
        using var app = new TestAppFactory();
        var client = app.CreateClient();
        // Primary rechaza siempre → se rutea la MISMA key al alternativo, que cobra.
        await client.PostAsJsonAsync("/demo/processor-behavior",
            new ProcessorBehaviorRequest("primary", "fail", 1000, "PAID"));

        var id = await CrearPago(client);
        var p = await EsperarEstado(app, id, PaymentState.Pagado);

        Assert.Equal(PaymentState.Pagado, p.State);
        Assert.Equal("alternative", p.ProcessorUsed);
    }

    [Fact]
    public async Task ProcesadorTimeout_DejaEstadoINCIERTO_NoFALLIDO()
    {
        using var app = new TestAppFactory();
        var client = app.CreateClient();
        // Primary y alternative timeoutean, y el status queda UNKNOWN: no se puede confirmar.
        await client.PostAsJsonAsync("/demo/processor-behavior",
            new ProcessorBehaviorRequest("primary", "timeout", 1000, "UNKNOWN"));
        await client.PostAsJsonAsync("/demo/processor-behavior",
            new ProcessorBehaviorRequest("alternative", "timeout", 1000, "UNKNOWN"));

        var id = await CrearPago(client);
        var p = await EsperarEstado(app, id, PaymentState.Incierto);

        Assert.Equal(PaymentState.Incierto, p.State); // timeout ≠ fallo
    }
}
