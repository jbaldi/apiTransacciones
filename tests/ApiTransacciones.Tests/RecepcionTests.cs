using System.Net;
using System.Net.Http.Json;
using ApiTransacciones.Api;
using ApiTransacciones.Domain;
using Xunit;

public class RecepcionTests
{
    [Fact]
    public async Task Recepcion_ConIdempotencyKey_Retorna202Pendiente()
    {
        using var app = new TestAppFactory();
        var client = app.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/payments")
        {
            Content = JsonContent.Create(new CreatePaymentRequest(1500m, "ARS", "cli-1"))
        };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<PaymentAccepted>();
        Assert.Equal(PaymentState.Pendiente, body!.State);
    }

    [Fact]
    public async Task SinIdempotencyKey_Retorna400()
    {
        using var app = new TestAppFactory();
        var client = app.CreateClient();
        var res = await client.PostAsJsonAsync("/payments", new CreatePaymentRequest(10m, "ARS", null));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task MismaKey_DevuelveMismoResultado_SinReprocesar()
    {
        using var app = new TestAppFactory();
        var client = app.CreateClient();
        var key = Guid.NewGuid().ToString();

        async Task<PaymentAccepted> Post()
        {
            var r = new HttpRequestMessage(HttpMethod.Post, "/payments")
            { Content = JsonContent.Create(new CreatePaymentRequest(1500m, "ARS", "cli-1")) };
            r.Headers.Add("Idempotency-Key", key);
            var res = await client.SendAsync(r);
            return (await res.Content.ReadFromJsonAsync<PaymentAccepted>())!;
        }

        var first = await Post();
        var second = await Post();
        Assert.Equal(first.PaymentId, second.PaymentId); // mismo pago, no se creó otro
    }
}
