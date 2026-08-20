using ApiTransacciones.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

/// Factory de tests: cada test usa un archivo SQLite temporal propio (permite múltiples
/// conexiones y transacciones reales, a diferencia de :memory: single-connection, que no es
/// seguro entre el request y los workers en background). El reloj real se reemplaza por
/// FakeTimeProvider para no depender de esperas reales.
public class TestAppFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"pagos-test-{Guid.NewGuid():N}.db");
    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.Parse("2026-08-20T12:00:00Z"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<PaymentsDbContext>>();
            services.AddDbContext<PaymentsDbContext>(o => o.UseSqlite($"DataSource={_dbPath}"));
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);

            using var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();
            scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Database.EnsureCreated();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && File.Exists(_dbPath))
            try { File.Delete(_dbPath); } catch { /* best-effort */ }
    }
}
