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
    }
}
