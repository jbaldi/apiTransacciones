using ApiTransacciones.Api;
using ApiTransacciones.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<PaymentsDbContext>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Db") ?? "DataSource=pagos.db"));
builder.Services.AddScoped<EventLog>();
builder.Services.AddSingleton(TimeProvider.System);

var app = builder.Build();

// Crear la BD al arrancar (demo sin migraciones).
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Database.EnsureCreated();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapPayments();

app.Run();

public partial class Program { }
