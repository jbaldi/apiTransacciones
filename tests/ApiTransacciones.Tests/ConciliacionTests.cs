using System.Net.Http.Json;
using ApiTransacciones.Api;
using ApiTransacciones.Domain;
using ApiTransacciones.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public class ConciliacionTests
{
    private static async Task<Guid> CrearPago(HttpClient client)
    {
        var r = new HttpRequestMessage(HttpMethod.Post, "/payments")
        { Content = JsonContent.Create(new CreatePaymentRequest(1500m, "ARS", "cli")) };
        r.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var res = await client.SendAsync(r);
        return (await res.Content.ReadFromJsonAsync<PaymentAccepted>())!.PaymentId;
    }

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
    public async Task Conciliacion_DefineElDinero_StatusPAID_MarcaPAGADO()
    {
        using var app = new TestAppFactory();
        var client = app.CreateClient();
        // Timeout al cobrar → INCIERTO, pero el status real dice PAID → la conciliación marca PAGADO.
        await client.PostAsJsonAsync("/demo/processor-behavior",
            new ProcessorBehaviorRequest("primary", "timeout", 1000, "PAID"));
        await client.PostAsJsonAsync("/demo/processor-behavior",
            new ProcessorBehaviorRequest("alternative", "timeout", 1000, "PAID"));

        var id = await CrearPago(client);
        var p = await EsperarEstado(app, id, PaymentState.Pagado);
        Assert.Equal(PaymentState.Pagado, p.State);
    }

    [Fact]
    public async Task Conciliacion_StatusFAILED_CompensaAFallido()
    {
        using var app = new TestAppFactory();
        var client = app.CreateClient();
        await client.PostAsJsonAsync("/demo/processor-behavior",
            new ProcessorBehaviorRequest("primary", "timeout", 1000, "FAILED"));
        await client.PostAsJsonAsync("/demo/processor-behavior",
            new ProcessorBehaviorRequest("alternative", "timeout", 1000, "FAILED"));

        var id = await CrearPago(client);
        var p = await EsperarEstado(app, id, PaymentState.Fallido);
        Assert.Equal(PaymentState.Fallido, p.State);
    }
}
