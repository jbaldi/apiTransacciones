var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

// Smoke test de arranque
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

// Necesario para que WebApplicationFactory<Program> compile en los tests.
public partial class Program { }
