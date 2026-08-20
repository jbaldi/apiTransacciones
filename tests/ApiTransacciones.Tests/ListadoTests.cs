using System.Net.Http.Json;
using ApiTransacciones.Api;
using Xunit;

public class ListadoTests
{
    private static HttpRequestMessage NuevoPago(decimal amount)
    {
        var r = new HttpRequestMessage(HttpMethod.Post, "/payments")
        { Content = JsonContent.Create(new CreatePaymentRequest(amount, "ARS", "cli")) };
        r.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return r;
    }

    [Fact]
    public async Task Listar_DevuelveLosPagosCreados_MasNuevosPrimero()
    {
        using var app = new TestAppFactory();
        var client = app.CreateClient();

        await client.SendAsync(NuevoPago(100m));
        await client.SendAsync(NuevoPago(200m));

        var lista = await client.GetFromJsonAsync<List<PaymentListItem>>("/payments");

        Assert.NotNull(lista);
        Assert.True(lista!.Count >= 2);
        // Más nuevos primero: el createdAt del primero es >= al del segundo.
        Assert.True(lista[0].CreatedAt >= lista[1].CreatedAt);
        Assert.All(lista, p => Assert.False(string.IsNullOrEmpty(p.IdempotencyKey)));
    }

    [Fact]
    public async Task Limpiar_BorraTodosLosPagos_YDejaLaListaVacia()
    {
        using var app = new TestAppFactory();
        var client = app.CreateClient();
        await client.SendAsync(NuevoPago(100m));
        await client.SendAsync(NuevoPago(200m));

        var del = await client.DeleteAsync("/payments");
        del.EnsureSuccessStatusCode();

        var lista = await client.GetFromJsonAsync<List<PaymentListItem>>("/payments");
        Assert.NotNull(lista);
        Assert.Empty(lista!);
    }
}
