using ApiTransacciones.Api;
using ApiTransacciones.Persistence;
using ApiTransacciones.Processors;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<PaymentsDbContext>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Db") ?? "DataSource=pagos.db"));
builder.Services.AddScoped<EventLog>();
builder.Services.AddSingleton(TimeProvider.System);

// Procesadores falsos + sus guiones (singletons para conservar estado entre requests).
builder.Services.AddSingleton(sp =>
{
    var clock = sp.GetRequiredService<TimeProvider>();
    var primaryBehavior = new ProcessorBehavior();
    var altBehavior = new ProcessorBehavior();
    return new ProcessorRegistry
    {
        PrimaryBehavior = primaryBehavior,
        AlternativeBehavior = altBehavior,
        Primary = new FakeProcessor("primary", primaryBehavior, clock),
        Alternative = new FakeProcessor("alternative", altBehavior, clock)
    };
});

var app = builder.Build();

// Crear la BD al arrancar (demo sin migraciones).
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Database.EnsureCreated();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapPayments();
app.MapDemo();

app.Run();

public partial class Program { }
