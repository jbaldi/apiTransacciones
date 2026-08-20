using System.Net.Http.Json;
using ApiTransacciones.Api;
using ApiTransacciones.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public class OutboxTests
{
    [Fact]
    public async Task Payment_y_Outbox_SeInsertan_EnLaMismaTransaccion()
    {
        using var app = new TestAppFactory();
        var client = app.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/payments")
        { Content = JsonContent.Create(new CreatePaymentRequest(1500m, "ARS", "cli-1")) };
        req.Headers.Add("Idempotency-Key", System.Guid.NewGuid().ToString());
        await client.SendAsync(req);

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        Assert.Single(db.Payments);
        Assert.Single(db.Outbox); // por cada pago hay exactamente un mensaje de Outbox
        Assert.Equal(db.Payments.First().Id, db.Outbox.First().PaymentId);
    }
}
