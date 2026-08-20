# API de Transacciones de Pago — Plan de Implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Construir una API REST .NET 10 que procese pagos de forma resiliente (idempotencia end-to-end, Outbox, circuit breaker + ruteo, timeout→INCIERTO, conciliación como fuente de verdad) y una presentación web para LinkedIn.

**Architecture:** Recepción síncrona idempotente que persiste `Payment(PENDIENTE)` + `OutboxMessage` en una sola transacción y responde 202. Dos BackgroundServices asíncronos: `OutboxDispatcher` (envío con Polly + ruteo a procesador alternativo) y `ReconciliationWorker` (resuelve los INCIERTO consultando status). Procesadores falsos in-memory con "guiones" configurables. Todo auditado en un `EventLog` append-only.

**Tech Stack:** .NET 10 Minimal API, EF Core + SQLite, Polly v8 (`Microsoft.Extensions.Resilience` / `Polly.Core`), xUnit + `Microsoft.AspNetCore.Mvc.Testing` + `Microsoft.Extensions.TimeProvider.Testing`.

## Global Constraints

- Target framework: `net10.0` en todos los proyectos.
- Idioma: comentarios de código y mensajes de API en **español**; nombres de tipos/métodos en inglés técnico salvo los estados del pago.
- Estados del pago (string enum, valor exacto): `PENDIENTE`, `EN_PROCESO`, `PAGADO`, `FALLIDO`, `INCIERTO`.
- Sin infraestructura externa: SQLite (archivo o in-memory), cola en memoria. Nada de Docker/RabbitMQ/Postgres.
- El `EventLog` es append-only: solo INSERT, jamás UPDATE/DELETE.
- El dinero se da por `PAGADO` únicamente cuando el procesador lo confirma (Charge OK o conciliación PAID). Un timeout nunca marca FALLIDO ni PAGADO.
- Idempotency-key se propaga cliente→API (header `Idempotency-Key`) y API→procesador (parámetro de `Charge`).
- Commits frecuentes: uno por tarea como mínimo, en español, terminando con `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.

---

## Estructura de archivos

```
src/ApiTransacciones/
  ApiTransacciones.csproj
  Program.cs
  Domain/
    PaymentState.cs            (enum-string + parsing)
    Payment.cs                 (entidad + comportamiento de dominio)
    PaymentStateMachine.cs     (transiciones válidas)
    DomainEvents.cs            (nombres de eventos del EventLog)
  Persistence/
    PaymentsDbContext.cs       (DbSets + configuración + UNIQUE)
    OutboxMessage.cs
    EventLogEntry.cs
    EventLog.cs                (append-only)
  Processors/
    IPaymentProcessor.cs       (Charge, GetStatus, contratos de resultado)
    ProcessorBehavior.cs       (guion configurable: mode/failCount)
    FakeProcessor.cs           (base parametrizable → Primary y Alternative)
  Resilience/
    ResiliencePipelineFactory.cs (Polly: timeout+retry+breaker)
    ProcessorRouter.cs         (Primary vs Alternative según breaker)
  Workers/
    OutboxDispatcher.cs        (envío async)
    ReconciliationWorker.cs    (conciliación)
  Api/
    Dtos.cs                    (requests/responses)
    PaymentsEndpoints.cs       (POST /payments, GET /payments/{id}, /events)
    DemoEndpoints.cs           (POST /demo/processor-behavior)

tests/ApiTransacciones.Tests/
  ApiTransacciones.Tests.csproj
  TestAppFactory.cs            (WebApplicationFactory con SQLite in-memory + FakeTimeProvider)
  PaymentStateMachineTests.cs
  RecepcionTests.cs
  OutboxTests.cs
  EnvioTests.cs
  ConciliacionTests.cs
  EventLogTests.cs
```

---

### Task 0: Scaffold de la solución

**Files:**
- Create: `apiTransacciones.sln`, `src/ApiTransacciones/ApiTransacciones.csproj`, `src/ApiTransacciones/Program.cs`, `tests/ApiTransacciones.Tests/ApiTransacciones.Tests.csproj`

**Interfaces:**
- Produces: solución compilable con un endpoint `GET /health` para smoke test.

- [ ] **Step 1: Crear solución y proyectos**

```bash
cd /Users/jbaldi/Developer/net/apiTransacciones
dotnet new sln -n apiTransacciones
dotnet new web -n ApiTransacciones -o src/ApiTransacciones -f net10.0
dotnet new xunit -n ApiTransacciones.Tests -o tests/ApiTransacciones.Tests -f net10.0
dotnet sln add src/ApiTransacciones/ApiTransacciones.csproj tests/ApiTransacciones.Tests/ApiTransacciones.Tests.csproj
dotnet add tests/ApiTransacciones.Tests reference src/ApiTransacciones
```

- [ ] **Step 2: Agregar paquetes**

```bash
dotnet add src/ApiTransacciones package Microsoft.EntityFrameworkCore.Sqlite
dotnet add src/ApiTransacciones package Polly.Core
dotnet add src/ApiTransacciones package Microsoft.Extensions.Http.Resilience
dotnet add tests/ApiTransacciones.Tests package Microsoft.AspNetCore.Mvc.Testing
dotnet add tests/ApiTransacciones.Tests package Microsoft.EntityFrameworkCore.Sqlite
dotnet add tests/ApiTransacciones.Tests package Microsoft.Extensions.TimeProvider.Testing
```

- [ ] **Step 3: Program.cs mínimo con health y clase parcial para tests**

Reemplazar `src/ApiTransacciones/Program.cs`:

```csharp
var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

// Smoke test de arranque
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

// Necesario para que WebApplicationFactory<Program> compile en los tests.
public partial class Program { }
```

- [ ] **Step 4: Build y smoke test**

Run: `dotnet build`
Expected: build succeeded.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "chore: scaffold de la solución .NET 10 (api + tests + paquetes)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 1: Dominio — estados y máquina de estados

**Files:**
- Create: `src/ApiTransacciones/Domain/PaymentState.cs`, `src/ApiTransacciones/Domain/PaymentStateMachine.cs`
- Test: `tests/ApiTransacciones.Tests/PaymentStateMachineTests.cs`

**Interfaces:**
- Produces:
  - `static class PaymentState` con constantes `Pendiente="PENDIENTE"`, `EnProceso="EN_PROCESO"`, `Pagado="PAGADO"`, `Fallido="FALLIDO"`, `Incierto="INCIERTO"`.
  - `static class PaymentStateMachine` con `bool CanTransition(string from, string to)` y `void EnsureTransition(string from, string to)` (lanza `InvalidOperationException` si no es válida).

- [ ] **Step 1: Escribir el test que falla**

`tests/ApiTransacciones.Tests/PaymentStateMachineTests.cs`:

```csharp
using ApiTransacciones.Domain;
using Xunit;

public class PaymentStateMachineTests
{
    [Theory]
    [InlineData(PaymentState.Pendiente, PaymentState.EnProceso)]
    [InlineData(PaymentState.EnProceso, PaymentState.Pagado)]
    [InlineData(PaymentState.EnProceso, PaymentState.Fallido)]
    [InlineData(PaymentState.EnProceso, PaymentState.Incierto)]
    [InlineData(PaymentState.Incierto, PaymentState.Pagado)]
    [InlineData(PaymentState.Incierto, PaymentState.Fallido)]
    public void TransicionesValidas_SonPermitidas(string from, string to)
        => Assert.True(PaymentStateMachine.CanTransition(from, to));

    [Theory]
    [InlineData(PaymentState.Pagado, PaymentState.Pendiente)]
    [InlineData(PaymentState.Pagado, PaymentState.Fallido)]
    [InlineData(PaymentState.Fallido, PaymentState.Pagado)]
    [InlineData(PaymentState.Pendiente, PaymentState.Pagado)] // no se puede pagar sin pasar por EN_PROCESO
    public void TransicionInvalida_EsRechazada(string from, string to)
    {
        Assert.False(PaymentStateMachine.CanTransition(from, to));
        Assert.Throws<InvalidOperationException>(() => PaymentStateMachine.EnsureTransition(from, to));
    }
}
```

- [ ] **Step 2: Correr el test y verificar que falla**

Run: `dotnet test --filter PaymentStateMachineTests`
Expected: FAIL (no compila: `PaymentState`/`PaymentStateMachine` no existen).

- [ ] **Step 3: Implementar el dominio**

`src/ApiTransacciones/Domain/PaymentState.cs`:

```csharp
namespace ApiTransacciones.Domain;

/// Estados del ciclo de vida del pago. Valores string estables (persistidos y expuestos por API).
public static class PaymentState
{
    public const string Pendiente = "PENDIENTE";
    public const string EnProceso = "EN_PROCESO";
    public const string Pagado    = "PAGADO";
    public const string Fallido   = "FALLIDO";
    public const string Incierto  = "INCIERTO";
}
```

`src/ApiTransacciones/Domain/PaymentStateMachine.cs`:

```csharp
namespace ApiTransacciones.Domain;

/// Máquina de estados (Saga) del pago. Sólo permite transiciones válidas:
/// el dinero nunca "salta" a PAGADO sin pasar por el procesador.
public static class PaymentStateMachine
{
    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        [PaymentState.Pendiente] = [PaymentState.EnProceso],
        [PaymentState.EnProceso] = [PaymentState.Pagado, PaymentState.Fallido, PaymentState.Incierto],
        [PaymentState.Incierto]  = [PaymentState.Pagado, PaymentState.Fallido], // resuelto por conciliación
        [PaymentState.Pagado]    = [],  // estado terminal
        [PaymentState.Fallido]   = [],  // estado terminal
    };

    public static bool CanTransition(string from, string to)
        => Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static void EnsureTransition(string from, string to)
    {
        if (!CanTransition(from, to))
            throw new InvalidOperationException($"Transición inválida: {from} → {to}");
    }
}
```

- [ ] **Step 4: Correr el test y verificar que pasa**

Run: `dotnet test --filter PaymentStateMachineTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: dominio de estados del pago + máquina de estados (Saga)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 2: Persistencia — entidades, DbContext y EventLog append-only

**Files:**
- Create: `src/ApiTransacciones/Domain/Payment.cs`, `src/ApiTransacciones/Persistence/OutboxMessage.cs`, `src/ApiTransacciones/Persistence/EventLogEntry.cs`, `src/ApiTransacciones/Persistence/PaymentsDbContext.cs`, `src/ApiTransacciones/Persistence/EventLog.cs`
- Test: `tests/ApiTransacciones.Tests/EventLogTests.cs`

**Interfaces:**
- Consumes: `PaymentState`, `PaymentStateMachine`.
- Produces:
  - `class Payment` con `Guid Id`, `string IdempotencyKey`, `decimal Amount`, `string Currency`, `string State`, `string? ProcessorUsed`, `string? ProcessorRef`, `int Attempts`, `DateTimeOffset CreatedAt`, `DateTimeOffset UpdatedAt`, `string? ResponseSnapshot`; método `void TransitionTo(string newState, TimeProvider clock)`.
  - `class OutboxMessage` con `Guid Id`, `Guid PaymentId`, `string Type`, `string Payload`, `string Status` (`PENDING/DISPATCHED/FAILED`), `DateTimeOffset CreatedAt`, `DateTimeOffset? DispatchedAt`, `int RetryCount`.
  - `class EventLogEntry` con `long Id`, `Guid PaymentId`, `string EventType`, `string Data`, `DateTimeOffset OccurredAt`.
  - `class PaymentsDbContext : DbContext` con `DbSet<Payment> Payments`, `DbSet<OutboxMessage> Outbox`, `DbSet<EventLogEntry> Events`.
  - `class EventLog(PaymentsDbContext db, TimeProvider clock)` con `Task AppendAsync(Guid paymentId, string eventType, object? data = null, CancellationToken ct = default)`.

- [ ] **Step 1: Escribir el test que falla**

`tests/ApiTransacciones.Tests/EventLogTests.cs`:

```csharp
using ApiTransacciones.Domain;
using ApiTransacciones.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public class EventLogTests
{
    private static PaymentsDbContext NewDb(SqliteConnection conn)
    {
        var options = new DbContextOptionsBuilder<PaymentsDbContext>().UseSqlite(conn).Options;
        var db = new PaymentsDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    [Fact]
    public async Task EventLog_EsInmutable_SoloAppend_YMantieneOrden()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var clock = new FakeTimeProvider();
        using var db = NewDb(conn);
        var log = new EventLog(db, clock);
        var pid = Guid.NewGuid();

        await log.AppendAsync(pid, DomainEvents.PaymentReceived);
        clock.Advance(TimeSpan.FromSeconds(1));
        await log.AppendAsync(pid, DomainEvents.SentToProcessor, new { processor = "primary" });

        var events = await db.Events.Where(e => e.PaymentId == pid).OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.Equal(DomainEvents.PaymentReceived, events[0].EventType);
        Assert.True(events[1].Id > events[0].Id); // orden garantizado por autoincremento
    }
}
```

- [ ] **Step 2: Correr y verificar que falla**

Run: `dotnet test --filter EventLogTests`
Expected: FAIL (no compilan los tipos).

- [ ] **Step 3: Implementar entidades, contexto, EventLog y nombres de eventos**

`src/ApiTransacciones/Domain/DomainEvents.cs`:

```csharp
namespace ApiTransacciones.Domain;

/// Nombres estables de los eventos que van al EventLog inmutable.
public static class DomainEvents
{
    public const string PaymentReceived         = "PaymentReceived";
    public const string SentToProcessor         = "SentToProcessor";
    public const string ProcessorSucceeded      = "ProcessorSucceeded";
    public const string ProcessorFailed         = "ProcessorFailed";
    public const string ProcessorTimeout        = "ProcessorTimeout";
    public const string MarkedUncertain         = "MarkedUncertain";
    public const string RoutedToAlternative     = "RoutedToAlternative";
    public const string ReconciliationChecked   = "ReconciliationChecked";
    public const string ReconciliationConfirmed = "ReconciliationConfirmed";
    public const string StateChanged            = "StateChanged";
}
```

`src/ApiTransacciones/Domain/Payment.cs`:

```csharp
namespace ApiTransacciones.Domain;

/// Entidad raíz del pago. El cambio de estado pasa siempre por la máquina de estados.
public class Payment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string IdempotencyKey { get; set; } = default!;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "ARS";
    public string State { get; set; } = PaymentState.Pendiente;
    public string? ProcessorUsed { get; set; }
    public string? ProcessorRef { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? ResponseSnapshot { get; set; }

    /// Cambia de estado validando la transición. Devuelve el estado anterior.
    public string TransitionTo(string newState, TimeProvider clock)
    {
        PaymentStateMachine.EnsureTransition(State, newState);
        var previous = State;
        State = newState;
        UpdatedAt = clock.GetUtcNow();
        return previous;
    }
}
```

`src/ApiTransacciones/Persistence/OutboxMessage.cs`:

```csharp
namespace ApiTransacciones.Persistence;

/// Mensaje del patrón Outbox: se persiste en la MISMA transacción que el Payment.
public class OutboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PaymentId { get; set; }
    public string Type { get; set; } = default!;
    public string Payload { get; set; } = "{}";
    public string Status { get; set; } = OutboxStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DispatchedAt { get; set; }
    public int RetryCount { get; set; }
}

public static class OutboxStatus
{
    public const string Pending    = "PENDING";
    public const string Dispatched = "DISPATCHED";
    public const string Failed     = "FAILED";
}
```

`src/ApiTransacciones/Persistence/EventLogEntry.cs`:

```csharp
namespace ApiTransacciones.Persistence;

/// Entrada del log de auditoría inmutable (append-only).
public class EventLogEntry
{
    public long Id { get; set; } // autoincremento → orden garantizado
    public Guid PaymentId { get; set; }
    public string EventType { get; set; } = default!;
    public string Data { get; set; } = "{}";
    public DateTimeOffset OccurredAt { get; set; }
}
```

`src/ApiTransacciones/Persistence/PaymentsDbContext.cs`:

```csharp
using ApiTransacciones.Domain;
using Microsoft.EntityFrameworkCore;

namespace ApiTransacciones.Persistence;

public class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<EventLogEntry> Events => Set<EventLogEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Idempotencia: una key no puede repetirse. Corazón de "misma key → mismo resultado".
        b.Entity<Payment>().HasIndex(p => p.IdempotencyKey).IsUnique();
        b.Entity<Payment>().Property(p => p.Amount).HasConversion<double>(); // SQLite no tiene decimal nativo
        b.Entity<OutboxMessage>().HasIndex(o => o.Status);
        b.Entity<EventLogEntry>().Property(e => e.Id).ValueGeneratedOnAdd();
    }
}
```

`src/ApiTransacciones/Persistence/EventLog.cs`:

```csharp
using System.Text.Json;

namespace ApiTransacciones.Persistence;

/// Log de auditoría append-only. Sólo inserta; nunca actualiza ni borra.
public class EventLog(PaymentsDbContext db, TimeProvider clock)
{
    public async Task AppendAsync(Guid paymentId, string eventType, object? data = null, CancellationToken ct = default)
    {
        db.Events.Add(new EventLogEntry
        {
            PaymentId = paymentId,
            EventType = eventType,
            Data = data is null ? "{}" : JsonSerializer.Serialize(data),
            OccurredAt = clock.GetUtcNow()
        });
        await db.SaveChangesAsync(ct);
    }
}
```

- [ ] **Step 4: Correr y verificar que pasa**

Run: `dotnet test --filter EventLogTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: persistencia (SQLite/EF Core) con Payment, Outbox y EventLog append-only

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 3: Recepción — endpoint idempotente con Outbox atómico

**Files:**
- Create: `src/ApiTransacciones/Api/Dtos.cs`, `src/ApiTransacciones/Api/PaymentsEndpoints.cs`, `tests/ApiTransacciones.Tests/TestAppFactory.cs`
- Modify: `src/ApiTransacciones/Program.cs`
- Test: `tests/ApiTransacciones.Tests/RecepcionTests.cs`, `tests/ApiTransacciones.Tests/OutboxTests.cs`

**Interfaces:**
- Consumes: `Payment`, `OutboxMessage`, `EventLog`, `PaymentsDbContext`.
- Produces:
  - `record CreatePaymentRequest(decimal Amount, string Currency, string? CustomerId)`.
  - `record PaymentAccepted(Guid PaymentId, string State, string Message)`.
  - `record PaymentView(Guid PaymentId, string State, string? ProcessorUsed, int Attempts)`.
  - `static class PaymentsEndpoints` con `void MapPayments(this WebApplication app)` (por ahora sólo `POST /payments`).
  - `class TestAppFactory : WebApplicationFactory<Program>` que sustituye la BD por SQLite in-memory compartida y expone `FakeTimeProvider Clock`.

- [ ] **Step 1: Escribir tests que fallan (recepción + outbox atómico + idempotencia)**

`tests/ApiTransacciones.Tests/TestAppFactory.cs`:

```csharp
using ApiTransacciones.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

/// Factory de tests: reemplaza SQLite por una conexión in-memory compartida (viva mientras dure el test)
/// y el reloj real por FakeTimeProvider para controlar backoffs y conciliación sin esperas reales.
public class TestAppFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.Parse("2026-08-20T12:00:00Z"));

    public TestAppFactory() => _conn.Open();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<PaymentsDbContext>>();
            services.AddDbContext<PaymentsDbContext>(o => o.UseSqlite(_conn));
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
        if (disposing) _conn.Dispose();
    }
}
```

`tests/ApiTransacciones.Tests/RecepcionTests.cs`:

```csharp
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
```

`tests/ApiTransacciones.Tests/OutboxTests.cs`:

```csharp
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
```

- [ ] **Step 2: Correr y verificar que fallan**

Run: `dotnet test --filter "RecepcionTests|OutboxTests"`
Expected: FAIL (no compila / endpoint inexistente).

- [ ] **Step 3: Implementar DTOs, endpoint y registro DI**

`src/ApiTransacciones/Api/Dtos.cs`:

```csharp
namespace ApiTransacciones.Api;

public record CreatePaymentRequest(decimal Amount, string Currency, string? CustomerId);
public record PaymentAccepted(Guid PaymentId, string State, string Message);
public record PaymentView(Guid PaymentId, string State, string? ProcessorUsed, int Attempts);
```

`src/ApiTransacciones/Api/PaymentsEndpoints.cs`:

```csharp
using System.Text.Json;
using ApiTransacciones.Domain;
using ApiTransacciones.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ApiTransacciones.Api;

public static class PaymentsEndpoints
{
    public static void MapPayments(this WebApplication app)
    {
        // MOMENTO 1 · RECEPCIÓN: nunca perder el pago. No hablamos con nadie externo acá.
        app.MapPost("/payments", async (
            HttpRequest http,
            CreatePaymentRequest body,
            PaymentsDbContext db,
            EventLog log,
            TimeProvider clock,
            CancellationToken ct) =>
        {
            // Idempotency-key end-to-end: obligatoria del lado del cliente.
            if (!http.Headers.TryGetValue("Idempotency-Key", out var keyValues) ||
                string.IsNullOrWhiteSpace(keyValues))
                return Results.BadRequest(new { error = "Idempotency-Key requerida" });

            var key = keyValues.ToString();

            // Misma key → devolvemos el resultado existente. No reprocesamos.
            var existing = await db.Payments.FirstOrDefaultAsync(p => p.IdempotencyKey == key, ct);
            if (existing is not null)
                return Results.Ok(new PaymentAccepted(existing.Id, existing.State, "pago ya recibido"));

            var now = clock.GetUtcNow();
            var payment = new Payment
            {
                IdempotencyKey = key,
                Amount = body.Amount,
                Currency = body.Currency,
                State = PaymentState.Pendiente,
                CreatedAt = now,
                UpdatedAt = now
            };

            // PATRÓN OUTBOX: el pago y el evento se persisten en UNA sola transacción.
            // Si el proceso se cae justo después del 202, el mensaje no se pierde.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.Payments.Add(payment);
            db.Outbox.Add(new OutboxMessage
            {
                PaymentId = payment.Id,
                Type = DomainEvents.PaymentReceived,
                Payload = JsonSerializer.Serialize(new { payment.Id, payment.IdempotencyKey, payment.Amount }),
                Status = OutboxStatus.Pending,
                CreatedAt = now
            });
            db.Events.Add(new EventLogEntry
            {
                PaymentId = payment.Id,
                EventType = DomainEvents.PaymentReceived,
                Data = "{}",
                OccurredAt = now
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            // No colgamos al usuario: "recibido, en proceso".
            return Results.Accepted($"/payments/{payment.Id}",
                new PaymentAccepted(payment.Id, payment.State, "recibido, en proceso"));
        });
    }
}
```

`src/ApiTransacciones/Program.cs` (reemplazar):

```csharp
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
```

- [ ] **Step 4: Correr y verificar que pasan**

Run: `dotnet test --filter "RecepcionTests|OutboxTests"`
Expected: PASS (todos).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: recepción idempotente con Outbox atómico (POST /payments → 202)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 4: Procesadores falsos con guiones + endpoint de demo

**Files:**
- Create: `src/ApiTransacciones/Processors/IPaymentProcessor.cs`, `src/ApiTransacciones/Processors/ProcessorBehavior.cs`, `src/ApiTransacciones/Processors/FakeProcessor.cs`, `src/ApiTransacciones/Api/DemoEndpoints.cs`
- Modify: `src/ApiTransacciones/Program.cs`

**Interfaces:**
- Produces:
  - `enum ChargeOutcome { Ok, Failed, Timeout }` y `record ChargeResult(ChargeOutcome Outcome, string? ProcessorRef, string? Reason)`.
  - `enum ProcessorStatus { Paid, Failed, Unknown }`.
  - `interface IPaymentProcessor { string Name { get; } Task<ChargeResult> ChargeAsync(string idempotencyKey, decimal amount, CancellationToken ct); Task<ProcessorStatus> GetStatusAsync(string processorRef, CancellationToken ct); }`.
  - `class ProcessorBehavior` mutable con `string Mode` (`ok/fail/timeout/slow`), `int FailCount`, `ProcessorStatus StatusResult`; método `void Set(string mode, int failCount)`.
  - `class FakeProcessor(string name, ProcessorBehavior behavior, TimeProvider clock) : IPaymentProcessor`.
  - registro DI con dos instancias nombradas: `"primary"` y `"alternative"`; un `ProcessorRegistry` que las expone y guarda su `ProcessorBehavior`.

- [ ] **Step 1: Implementar contratos y procesador falso**

`src/ApiTransacciones/Processors/IPaymentProcessor.cs`:

```csharp
namespace ApiTransacciones.Processors;

public enum ChargeOutcome { Ok, Failed, Timeout }
public record ChargeResult(ChargeOutcome Outcome, string? ProcessorRef, string? Reason);

public enum ProcessorStatus { Paid, Failed, Unknown }

/// Contrato del procesador externo (el "tercero"). Charge cobra; GetStatus es la fuente de verdad.
public interface IPaymentProcessor
{
    string Name { get; }
    Task<ChargeResult> ChargeAsync(string idempotencyKey, decimal amount, CancellationToken ct);
    Task<ProcessorStatus> GetStatusAsync(string processorRef, CancellationToken ct);
}
```

`src/ApiTransacciones/Processors/ProcessorBehavior.cs`:

```csharp
namespace ApiTransacciones.Processors;

/// "Guion" configurable del procesador falso, para forzar escenarios en la demo.
/// mode: ok | fail | timeout | slow. failCount: cuántas veces falla/timeoutea antes de portarse bien.
public class ProcessorBehavior
{
    public string Mode { get; private set; } = "ok";
    public int FailCount { get; private set; }
    public ProcessorStatus StatusResult { get; private set; } = ProcessorStatus.Paid;

    private int _remaining;

    public void Set(string mode, int failCount, ProcessorStatus statusResult = ProcessorStatus.Paid)
    {
        Mode = mode;
        FailCount = failCount;
        _remaining = failCount;
        StatusResult = statusResult;
    }

    /// Consume un intento: devuelve true si este intento debe fallar/timeoutear según el guion.
    public bool ShouldMisbehaveOnce()
    {
        if (_remaining <= 0) return false;
        _remaining--;
        return true;
    }
}
```

`src/ApiTransacciones/Processors/FakeProcessor.cs`:

```csharp
namespace ApiTransacciones.Processors;

/// Procesador de pago FALSO e in-memory. Simula latencia, fallos y timeouts según su guion.
public class FakeProcessor(string name, ProcessorBehavior behavior, TimeProvider clock) : IPaymentProcessor
{
    public string Name => name;

    public async Task<ChargeResult> ChargeAsync(string idempotencyKey, decimal amount, CancellationToken ct)
    {
        var misbehave = behavior.ShouldMisbehaveOnce();
        switch (behavior.Mode)
        {
            case "timeout" when misbehave:
                await Task.Delay(TimeSpan.FromSeconds(30), clock, ct); // será cortado por el timeout de Polly
                throw new TimeoutException();
            case "fail" when misbehave:
                return new ChargeResult(ChargeOutcome.Failed, null, "rechazado por el procesador");
            case "slow":
                await Task.Delay(TimeSpan.FromMilliseconds(200), clock, ct);
                break;
        }
        // Éxito: devolvemos una referencia de operación (clave para conciliar después).
        return new ChargeResult(ChargeOutcome.Ok, $"{name}-{idempotencyKey}", null);
    }

    // Fuente de verdad del dinero: el estado real de la operación según el procesador.
    public Task<ProcessorStatus> GetStatusAsync(string processorRef, CancellationToken ct)
        => Task.FromResult(behavior.StatusResult);
}
```

`ProcessorRegistry` (agregar al final de `FakeProcessor.cs`):

```csharp
namespace ApiTransacciones.Processors;

/// Registro de procesadores nombrados + sus guiones, para el router y el endpoint de demo.
public class ProcessorRegistry
{
    public required IPaymentProcessor Primary { get; init; }
    public required IPaymentProcessor Alternative { get; init; }
    public required ProcessorBehavior PrimaryBehavior { get; init; }
    public required ProcessorBehavior AlternativeBehavior { get; init; }

    public ProcessorBehavior BehaviorFor(string processor) =>
        processor.Equals("alternative", StringComparison.OrdinalIgnoreCase)
            ? AlternativeBehavior : PrimaryBehavior;
}
```

- [ ] **Step 2: Registrar en DI y exponer endpoint de demo**

`src/ApiTransacciones/Api/DemoEndpoints.cs`:

```csharp
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
```

Agregar a `Program.cs` el registro DI (antes de `var app = builder.Build();`):

```csharp
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
```

Y `app.MapDemo();` junto a los otros `Map`. Agregar `using ApiTransacciones.Processors;` arriba.

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: build succeeded.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat: procesadores de pago falsos con guiones + endpoint /demo/processor-behavior

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 5: Resiliencia — pipeline de Polly + ProcessorRouter

**Files:**
- Create: `src/ApiTransacciones/Resilience/ResiliencePipelineFactory.cs`, `src/ApiTransacciones/Resilience/ProcessorRouter.cs`
- Modify: `src/ApiTransacciones/Program.cs`
- Test: `tests/ApiTransacciones.Tests/EnvioTests.cs` (parte 1: ruteo)

**Interfaces:**
- Consumes: `IPaymentProcessor`, `ProcessorRegistry`, `ChargeResult`, `ChargeOutcome`.
- Produces:
  - `class ResiliencePipelineFactory` con `ResiliencePipeline Build()` (timeout corto + retry backoff exponencial + circuit breaker) y propiedad `CircuitBreakerStateProvider Breaker` para inspeccionar el estado.
  - `class ProcessorRouter(ProcessorRegistry reg, ResiliencePipelineFactory pipelines)` con `Task<(ChargeResult Result, string ProcessorUsed)> ChargeAsync(string idempotencyKey, decimal amount, CancellationToken ct)`. Si el breaker del primary está abierto, rutea al alternative.

- [ ] **Step 1: Escribir el test que falla (breaker abre y rutea)**

`tests/ApiTransacciones.Tests/EnvioTests.cs`:

```csharp
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
        for (var i = 0; i < 50; i++)
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
    public async Task Breaker_SeAbre_TrasFallos_YRuteaAlAlternativo()
    {
        using var app = new TestAppFactory();
        var client = app.CreateClient();
        // Primary siempre falla → breaker abre → OpenPass (alternative) cobra.
        await client.PostAsJsonAsync("/demo/processor-behavior",
            new ProcessorBehaviorRequest("primary", "fail", 1000, "PAID"));

        var id = await CrearPago(client);
        var p = await EsperarEstado(app, id, PaymentState.Pagado);

        Assert.Equal(PaymentState.Pagado, p.State);
        Assert.Equal("alternative", p.ProcessorUsed);
    }
}
```

> Nota: este test depende del `OutboxDispatcher` de la Task 6. Se escribe ahora pero se marca en verde recién al terminar la Task 6. En la Task 5 sólo se verifica que compila el router y su test unitario propio (abajo).

- [ ] **Step 2: Implementar la fábrica de pipelines**

`src/ApiTransacciones/Resilience/ResiliencePipelineFactory.cs`:

```csharp
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace ApiTransacciones.Resilience;

/// Construye el pipeline de resiliencia del envío: timeout corto + reintentos con backoff
/// exponencial + circuit breaker. El breaker se expone para que el router pueda rutear.
public class ResiliencePipelineFactory
{
    public CircuitBreakerStateProvider Breaker { get; } = new();

    public ResiliencePipeline Build() =>
        new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential, // backoff exponencial
                Delay = TimeSpan.FromMilliseconds(200),
                UseJitter = true,
                ShouldHandle = new PredicateBuilder().Handle<Exception>()
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                MinimumThroughput = 2,
                SamplingDuration = TimeSpan.FromSeconds(10),
                BreakDuration = TimeSpan.FromSeconds(5),
                StateProvider = Breaker,
                ShouldHandle = new PredicateBuilder().Handle<Exception>()
            })
            .AddTimeout(TimeSpan.FromSeconds(2)) // timeout corto: no colgamos
            .Build();
}
```

`src/ApiTransacciones/Resilience/ProcessorRouter.cs`:

```csharp
using ApiTransacciones.Processors;
using Polly.CircuitBreaker;

namespace ApiTransacciones.Resilience;

/// Rutea el cobro: usa el primary a través del pipeline; si el breaker está abierto
/// (o el primary revienta), va al alternative (OpenPass). Devuelve qué procesador cobró.
public class ProcessorRouter(ProcessorRegistry reg, ResiliencePipelineFactory pipelines)
{
    public async Task<(ChargeResult Result, string ProcessorUsed)> ChargeAsync(
        string idempotencyKey, decimal amount, CancellationToken ct)
    {
        // Breaker abierto → no intentamos el primary, vamos directo al alternativo.
        if (pipelines.Breaker.CircuitState == CircuitState.Open)
        {
            var alt0 = await reg.Alternative.ChargeAsync(idempotencyKey, amount, ct);
            return (alt0, reg.Alternative.Name);
        }

        try
        {
            var pipeline = pipelines.Build();
            var result = await pipeline.ExecuteAsync(
                async token => await reg.Primary.ChargeAsync(idempotencyKey, amount, token), ct);

            // Fallo "claro" (rechazo): reenviamos la MISMA key al alternativo.
            if (result.Outcome == ChargeOutcome.Failed)
            {
                var alt = await reg.Alternative.ChargeAsync(idempotencyKey, amount, ct);
                return (alt, reg.Alternative.Name);
            }
            return (result, reg.Primary.Name);
        }
        catch (BrokenCircuitException)
        {
            var alt = await reg.Alternative.ChargeAsync(idempotencyKey, amount, ct);
            return (alt, reg.Alternative.Name);
        }
        catch (Exception)
        {
            // Timeout u otra excepción tras agotar reintentos → NO asumimos. Señalizamos timeout.
            return (new ChargeResult(ChargeOutcome.Timeout, null, "timeout tras reintentos"), reg.Primary.Name);
        }
    }
}
```

- [ ] **Step 3: Registrar en DI**

En `Program.cs` agregar (antes de build):

```csharp
builder.Services.AddSingleton<ResiliencePipelineFactory>();
builder.Services.AddSingleton(sp => new ProcessorRouter(
    sp.GetRequiredService<ProcessorRegistry>(),
    sp.GetRequiredService<ResiliencePipelineFactory>()));
```
(agregar `using ApiTransacciones.Resilience;`)

- [ ] **Step 4: Build (los tests de envío quedan rojos hasta la Task 6)**

Run: `dotnet build`
Expected: build succeeded.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: resiliencia con Polly (timeout+retry+circuit breaker) y ProcessorRouter

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 6: Envío asíncrono — OutboxDispatcher

**Files:**
- Create: `src/ApiTransacciones/Workers/OutboxDispatcher.cs`
- Modify: `src/ApiTransacciones/Program.cs`
- Test: reactiva `EnvioTests` + agrega `ProcesadorTimeout_DejaEstadoINCIERTO_NoFALLIDO`

**Interfaces:**
- Consumes: `PaymentsDbContext`, `EventLog`, `ProcessorRouter`, `ChargeOutcome`, `PaymentState`, `OutboxStatus`.
- Produces: `class OutboxDispatcher(IServiceProvider sp, TimeProvider clock) : BackgroundService` que lee `OutboxMessage` PENDING, cobra vía router y transiciona el pago.

- [ ] **Step 1: Agregar el test de timeout→INCIERTO en `EnvioTests.cs`**

```csharp
    [Fact]
    public async Task ProcesadorTimeout_DejaEstadoINCIERTO_NoFALLIDO()
    {
        using var app = new TestAppFactory();
        var client = app.CreateClient();
        // Primary siempre timeout; alternative también, para que no lo "salve".
        await client.PostAsJsonAsync("/demo/processor-behavior",
            new ProcessorBehaviorRequest("primary", "timeout", 1000, "UNKNOWN"));
        await client.PostAsJsonAsync("/demo/processor-behavior",
            new ProcessorBehaviorRequest("alternative", "timeout", 1000, "UNKNOWN"));

        var id = await CrearPago(client);
        var p = await EsperarEstado(app, id, PaymentState.Incierto);

        Assert.Equal(PaymentState.Incierto, p.State); // timeout ≠ fallo
    }
```

- [ ] **Step 2: Correr y verificar que falla (no hay dispatcher)**

Run: `dotnet test --filter EnvioTests`
Expected: FAIL / timeout (los pagos nunca salen de PENDIENTE).

- [ ] **Step 3: Implementar el OutboxDispatcher**

`src/ApiTransacciones/Workers/OutboxDispatcher.cs`:

```csharp
using ApiTransacciones.Domain;
using ApiTransacciones.Persistence;
using ApiTransacciones.Processors;
using ApiTransacciones.Resilience;
using Microsoft.EntityFrameworkCore;

namespace ApiTransacciones.Workers;

/// MOMENTO 2 · ENVÍO (asíncrono). La "cola": lee el Outbox y despacha el cobro.
/// Traduce el resultado del procesador a un estado del pago SIN asumir nada ante timeouts.
public class OutboxDispatcher(IServiceProvider sp, TimeProvider clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessBatchAsync(stoppingToken); }
            catch { /* la demo tolera errores transitorios; el mensaje sigue PENDING */ }
            await Task.Delay(TimeSpan.FromMilliseconds(200), stoppingToken);
        }
    }

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var log = scope.ServiceProvider.GetRequiredService<EventLog>();
        var router = scope.ServiceProvider.GetRequiredService<ProcessorRouter>();

        var pending = await db.Outbox
            .Where(o => o.Status == OutboxStatus.Pending)
            .OrderBy(o => o.CreatedAt).Take(10).ToListAsync(ct);

        foreach (var msg in pending)
        {
            var payment = await db.Payments.FirstAsync(p => p.Id == msg.PaymentId, ct);
            payment.TransitionTo(PaymentState.EnProceso, clock);
            payment.Attempts++;
            await log.AppendAsync(payment.Id, DomainEvents.SentToProcessor, ct: ct);

            // Reenviamos la MISMA idempotency-key al procesador (idempotencia end-to-end).
            var (result, processorUsed) = await router.ChargeAsync(payment.IdempotencyKey, payment.Amount, ct);
            payment.ProcessorUsed = processorUsed;

            switch (result.Outcome)
            {
                case ChargeOutcome.Ok:
                    payment.ProcessorRef = result.ProcessorRef;
                    payment.TransitionTo(PaymentState.Pagado, clock);
                    await log.AppendAsync(payment.Id, DomainEvents.ProcessorSucceeded, new { processorUsed }, ct);
                    break;

                case ChargeOutcome.Failed:
                    payment.TransitionTo(PaymentState.Fallido, clock);
                    await log.AppendAsync(payment.Id, DomainEvents.ProcessorFailed, new { result.Reason }, ct);
                    break;

                case ChargeOutcome.Timeout:
                    // Un timeout NO significa que falló: significa que no sé. Estado INCIERTO.
                    payment.ProcessorRef = result.ProcessorRef ?? $"{processorUsed}-{payment.IdempotencyKey}";
                    payment.TransitionTo(PaymentState.Incierto, clock);
                    await log.AppendAsync(payment.Id, DomainEvents.MarkedUncertain, ct: ct);
                    break;
            }

            msg.Status = OutboxStatus.Dispatched;
            msg.DispatchedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
    }
}
```

- [ ] **Step 4: Registrar el worker**

En `Program.cs`: `builder.Services.AddHostedService<OutboxDispatcher>();` (con `using ApiTransacciones.Workers;`).

> Nota para tests: `TestAppFactory` ya arranca los hosted services. Como usamos `FakeTimeProvider`, los `Task.Delay` del `FakeProcessor` (modo timeout/slow) usan ese reloj y no avanzan solos — por eso el timeout de Polly (2s, reloj real del pipeline) corta el intento. Verificar que el `AddTimeout` de Polly use el reloj real (default) y que el `Task.Delay` del worker use `Task.Delay(ms, stoppingToken)` con reloj real para no congelarse.

- [ ] **Step 5: Correr y verificar que pasan**

Run: `dotnet test --filter EnvioTests`
Expected: PASS (ruteo al alternativo y timeout→INCIERTO).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: envío asíncrono (OutboxDispatcher) con estados PAGADO/FALLIDO/INCIERTO

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 7: Conciliación — ReconciliationWorker

**Files:**
- Create: `src/ApiTransacciones/Workers/ReconciliationWorker.cs`
- Modify: `src/ApiTransacciones/Program.cs`
- Test: `tests/ApiTransacciones.Tests/ConciliacionTests.cs`

**Interfaces:**
- Consumes: `PaymentsDbContext`, `EventLog`, `ProcessorRegistry`, `ProcessorStatus`, `PaymentState`.
- Produces: `class ReconciliationWorker(IServiceProvider sp, TimeProvider clock) : BackgroundService` que toma pagos INCIERTO y consulta `GetStatusAsync`.

- [ ] **Step 1: Escribir los tests que fallan**

`tests/ApiTransacciones.Tests/ConciliacionTests.cs`:

```csharp
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
        // Timeout al cobrar → INCIERTO, pero el status real dice PAID → PAGADO.
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
```

- [ ] **Step 2: Correr y verificar que falla (queda en INCIERTO para siempre)**

Run: `dotnet test --filter ConciliacionTests`
Expected: FAIL / timeout.

- [ ] **Step 3: Implementar el ReconciliationWorker**

`src/ApiTransacciones/Workers/ReconciliationWorker.cs`:

```csharp
using ApiTransacciones.Domain;
using ApiTransacciones.Persistence;
using ApiTransacciones.Processors;
using Microsoft.EntityFrameworkCore;

namespace ApiTransacciones.Workers;

/// MOMENTO 3 · INCERTIDUMBRE. Toma los pagos INCIERTO y le pregunta al procesador
/// por el estado real de la operación. El dinero lo define el procesador, no mi suposición.
public class ReconciliationWorker(IServiceProvider sp, TimeProvider clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ReconcileAsync(stoppingToken); }
            catch { /* reintenta en el próximo ciclo */ }
            await Task.Delay(TimeSpan.FromMilliseconds(200), stoppingToken);
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var log = scope.ServiceProvider.GetRequiredService<EventLog>();
        var reg = scope.ServiceProvider.GetRequiredService<ProcessorRegistry>();

        var uncertain = await db.Payments
            .Where(p => p.State == PaymentState.Incierto)
            .Take(10).ToListAsync(ct);

        foreach (var payment in uncertain)
        {
            var processor = payment.ProcessorUsed == "alternative" ? reg.Alternative : reg.Primary;
            var status = await processor.GetStatusAsync(payment.ProcessorRef ?? "", ct);
            await log.AppendAsync(payment.Id, DomainEvents.ReconciliationChecked, new { status = status.ToString() }, ct);

            switch (status)
            {
                case ProcessorStatus.Paid:
                    payment.TransitionTo(PaymentState.Pagado, clock);
                    await log.AppendAsync(payment.Id, DomainEvents.ReconciliationConfirmed, new { estado = "PAGADO" }, ct);
                    break;
                case ProcessorStatus.Failed:
                    payment.TransitionTo(PaymentState.Fallido, clock); // compensación (Saga)
                    await log.AppendAsync(payment.Id, DomainEvents.ReconciliationConfirmed, new { estado = "FALLIDO" }, ct);
                    break;
                case ProcessorStatus.Unknown:
                    // Seguimos sin saber: NO tocamos el estado. Reintentamos en el próximo ciclo.
                    break;
            }
            await db.SaveChangesAsync(ct);
        }
    }
}
```

- [ ] **Step 4: Registrar el worker**

En `Program.cs`: `builder.Services.AddHostedService<ReconciliationWorker>();`.

- [ ] **Step 5: Correr y verificar que pasan**

Run: `dotnet test --filter ConciliacionTests`
Expected: PASS (PAID→PAGADO, FAILED→FALLIDO).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: conciliación (ReconciliationWorker) como fuente de verdad del dinero

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 8: Consulta de estado e historial de eventos

**Files:**
- Modify: `src/ApiTransacciones/Api/PaymentsEndpoints.cs`
- Test: agregar a `RecepcionTests.cs` un test de `GET /payments/{id}`

**Interfaces:**
- Consumes: `PaymentsDbContext`, `PaymentView`, `EventLogEntry`.
- Produces: `GET /payments/{id}` → `PaymentView`; `GET /payments/{id}/events` → lista de eventos.

- [ ] **Step 1: Escribir el test que falla**

Agregar en `RecepcionTests.cs`:

```csharp
    [Fact]
    public async Task ConsultarPago_DevuelveEstadoActual()
    {
        using var app = new TestAppFactory();
        var client = app.CreateClient();
        var post = new HttpRequestMessage(HttpMethod.Post, "/payments")
        { Content = System.Net.Http.Json.JsonContent.Create(new CreatePaymentRequest(1500m, "ARS", "cli")) };
        post.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var created = await (await client.SendAsync(post)).Content.ReadFromJsonAsync<PaymentAccepted>();

        var view = await client.GetFromJsonAsync<PaymentView>($"/payments/{created!.PaymentId}");
        Assert.Equal(created.PaymentId, view!.PaymentId);
    }
```

- [ ] **Step 2: Correr y verificar que falla**

Run: `dotnet test --filter ConsultarPago_DevuelveEstadoActual`
Expected: FAIL (404).

- [ ] **Step 3: Agregar endpoints de consulta**

En `PaymentsEndpoints.cs`, dentro de `MapPayments`:

```csharp
        // Consulta del estado actual del pago.
        app.MapGet("/payments/{id:guid}", async (Guid id, PaymentsDbContext db, CancellationToken ct) =>
        {
            var p = await db.Payments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            return p is null
                ? Results.NotFound()
                : Results.Ok(new PaymentView(p.Id, p.State, p.ProcessorUsed, p.Attempts));
        });

        // Historial inmutable de eventos (auditoría / replay).
        app.MapGet("/payments/{id:guid}/events", async (Guid id, PaymentsDbContext db, CancellationToken ct) =>
        {
            var events = await db.Events.AsNoTracking()
                .Where(e => e.PaymentId == id).OrderBy(e => e.Id)
                .Select(e => new { e.EventType, e.OccurredAt, e.Data })
                .ToListAsync(ct);
            return Results.Ok(events);
        });
```

- [ ] **Step 4: Correr toda la suite**

Run: `dotnet test`
Expected: PASS (todos los tests en verde).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: consulta de estado (GET /payments/{id}) e historial de eventos

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 9: README de demo + verificación end-to-end manual

**Files:**
- Create: `README.md`

**Interfaces:** ninguna (documentación).

- [ ] **Step 1: Escribir el README con los escenarios de demo**

Incluir: cómo correr (`dotnet run --project src/ApiTransacciones`), y los 4 guiones curl (idempotencia, timeout→INCIERTO, breaker→ruteo, conciliación PAID/FAILED), más `GET /payments/{id}/events` para mostrar la auditoría.

- [ ] **Step 2: Verificación manual end-to-end**

```bash
dotnet run --project src/ApiTransacciones &
# esperar arranque, luego:
curl -s -X POST localhost:5000/demo/processor-behavior -H 'Content-Type: application/json' \
  -d '{"processor":"primary","mode":"timeout","failCount":1000,"statusResult":"PAID"}'
KEY=$(uuidgen)
curl -s -X POST localhost:5000/payments -H "Idempotency-Key: $KEY" -H 'Content-Type: application/json' \
  -d '{"amount":1500,"currency":"ARS","customerId":"cli-1"}'
# consultar estado hasta ver PAGADO vía conciliación
```
Expected: el pago pasa PENDIENTE→EN_PROCESO→INCIERTO→PAGADO; los eventos lo reflejan.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "docs: README con escenarios de demo y verificación end-to-end

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 10: Presentación de LinkedIn (Artifact)

**Files:**
- Create: `docs/presentacion-linkedin.html` (artifact autocontenido)

**Interfaces:** ninguna.

- [ ] **Step 1: Cargar la skill de diseño de artifacts**

Invocar `artifact-design` antes de escribir el HTML.

- [ ] **Step 2: Construir el artifact** (theme-aware, autocontenido) con secciones:
  1. Portada: "Cómo diseñar una API de pagos que nunca pierde plata".
  2. El problema: los 3 momentos (recepción / envío / incertidumbre).
  3. Los 4 conceptos obligatorios + 2 bonus (Outbox, Saga).
  4. Diagrama de arquitectura (SVG inline).
  5. Máquina de estados (SVG inline).
  6. Snippets del código real (recepción idempotente, Outbox atómico, timeout→INCIERTO, conciliación).
  7. Los escenarios de demo y qué garantiza cada uno.

- [ ] **Step 3: Publicar el artifact** con `Artifact` y entregar la URL al usuario.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "docs: presentación de LinkedIn (artifact) de la arquitectura de pagos

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Self-Review (cobertura del spec)

- Idempotency-key end-to-end → Task 3 (header + UNIQUE) + Task 6 (misma key al procesador). ✔
- Timeout ≠ fallo → INCIERTO → Task 6 (`ChargeOutcome.Timeout`→INCIERTO), test en Task 6. ✔
- Conciliación como fuente de verdad → Task 7. ✔
- Circuit breaker + ruteo alternativo → Task 5 (pipeline+router) + Task 6 (test). ✔
- Outbox → Task 3 (misma transacción) + Task 6 (dispatcher). ✔
- Máquina de estados / Saga + compensación → Task 1 + Task 7 (FAILED compensa). ✔
- EventLog inmutable → Task 2. ✔
- Contratos de API (POST/GET/eventos/demo) → Tasks 3, 4, 8. ✔
- Testing (11 tests) → Tasks 1–8. ✔
- Presentación LinkedIn → Task 10. ✔

Sin placeholders. Tipos consistentes entre tareas (`ChargeResult`, `ProcessorStatus`, `PaymentState`, `ProcessorRegistry`, `ResiliencePipelineFactory.Breaker`).
