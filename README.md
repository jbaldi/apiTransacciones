# API de Transacciones de Pago

API REST en **.NET 10** que procesa pagos contra un procesador externo de forma
**resiliente y sin perder plata jamás**. Es una demo funcional (procesador y "cola"
simulados, sin infraestructura externa) que implementa los patrones clave de un
sistema de pagos serio.

## Los 3 momentos

1. **Recepción** — Persisto el pago con `Idempotency-Key` única en estado
   `PENDIENTE` antes de hablar con nadie externo. Misma key → devuelvo el
   resultado existente, no reproceso. Respondo `202 recibido, en proceso`.
2. **Envío** — El cobro va asíncrono (worker que lee el Outbox). Timeout corto +
   circuit breaker: si el procesador falla, abro el breaker y ruteo al procesador
   alternativo (OpenPass). Reintentos con backoff exponencial. Misma key al procesador.
3. **Incertidumbre** — Un timeout no significa que falló, significa que **no sé**.
   Dejo el pago en `INCIERTO` y disparo conciliación: le pregunto al procesador el
   estado real. El dinero lo define el procesador, no mi suposición.

## Los 4 conceptos

- **Idempotency-key end-to-end** (cliente↔API y API↔procesador).
- **Timeout ≠ fallo → `INCIERTO`** (nunca asumir).
- **Conciliación** como fuente de verdad del dinero.
- **Circuit breaker + ruteo a procesador alternativo**.

Bonus: **patrón Outbox** (pago + evento en la misma transacción) y **máquina de
estados / Saga** con compensaciones.

## Máquina de estados

```
PENDIENTE ──▶ EN_PROCESO ──▶ PAGADO
    │              │
    │              ├──▶ FALLIDO
    │              └──▶ INCIERTO ──(conciliación)──▶ PAGADO
                          │                          │
                          └──────────────────────────┴──▶ FALLIDO (compensación)
```

## Correr

```bash
dotnet run --project src/ApiTransacciones
```

Arranca en `http://localhost:5133` (o el puerto que indique la consola). Crea el
archivo `pagos.db` (SQLite) automáticamente.

## Consola web

Abrí `http://localhost:5133/` en el navegador: una **consola** (servida por la
misma API desde `wwwroot/`) para disparar pagos y ver todo en vivo:

- **Nuevo pago** con idempotency-key autogenerada, y botón para reenviar la misma
  key y ver la idempotencia en acción.
- **KPIs en vivo** con el conteo por estado y **tabla** con auto-refresh (1s):
  mirás los pagos pasar `PENDIENTE → EN_PROCESO → INCIERTO → PAGADO` solos.
- **Guión del procesador** + **escenarios rápidos** (Feliz, Timeout→conciliación,
  Breaker→alternativo, FAILED compensa) para forzar cada caso con un clic.
- Click en un pago → **detalle** con el log de eventos como comprobante de auditoría.
- Botón **🗑 Limpiar** (dos clics para confirmar) → resetea el tablero de la demo.

Endpoints que la alimentan: `GET /payments` (lista todos, más nuevos primero) y
`DELETE /payments` (limpia todo — sólo para la demo).

## Tests

```bash
dotnet test
```

## Escenarios de demo

El endpoint `POST /demo/processor-behavior` configura el "guion" del procesador
falso para forzar cada caso. `mode`: `ok | fail | timeout | slow`.
`statusResult` (lo que devuelve la conciliación): `PAID | FAILED | UNKNOWN`.

### 1. Feliz — cobra a la primera

```bash
KEY=$(uuidgen)
curl -X POST localhost:5000/payments -H "Idempotency-Key: $KEY" \
  -H 'Content-Type: application/json' \
  -d '{"amount":1500,"currency":"ARS","customerId":"c1"}'
# → 202 PENDIENTE; en un instante pasa a PAGADO (procesador primary)
```

### 2. Idempotencia — misma key no reprocesa

```bash
# Repetí el mismo POST con el MISMO $KEY → devuelve el mismo paymentId, no crea otro pago.
```

### 3. Circuit breaker + ruteo al alternativo

```bash
curl -X POST localhost:5000/demo/processor-behavior -H 'Content-Type: application/json' \
  -d '{"processor":"primary","mode":"fail","failCount":1000,"statusResult":"PAID"}'
# Nuevo pago → primary rechaza → se rutea la misma key al alternativo (OpenPass) → PAGADO
```

### 4. El caso feo — timeout → INCIERTO → conciliación

```bash
curl -X POST localhost:5000/demo/processor-behavior -H 'Content-Type: application/json' \
  -d '{"processor":"primary","mode":"timeout","failCount":1000,"statusResult":"PAID"}'
curl -X POST localhost:5000/demo/processor-behavior -H 'Content-Type: application/json' \
  -d '{"processor":"alternative","mode":"timeout","failCount":1000,"statusResult":"PAID"}'
# Nuevo pago → timeout → INCIERTO → la conciliación consulta status=PAID → PAGADO
# Si statusResult fuera FAILED, la conciliación compensa a FALLIDO.
```

### Ver estado e historial de auditoría

```bash
curl localhost:5000/payments/{id}
curl localhost:5000/payments/{id}/events   # log inmutable de eventos
```

## Estructura

```
src/ApiTransacciones/
  Domain/        Payment, PaymentState, PaymentStateMachine, DomainEvents
  Persistence/   PaymentsDbContext, OutboxMessage, EventLog(Entry)
  Processors/    IPaymentProcessor, FakeProcessor, ProcessorBehavior, ProcessorRegistry
  Resilience/    ResiliencePipelineFactory (Polly), ProcessorRouter
  Workers/       OutboxDispatcher, ReconciliationWorker
  Api/           PaymentsEndpoints, DemoEndpoints, Dtos
```

## Stack

.NET 10 Minimal API · EF Core + SQLite · Polly v8 (timeout + retry + circuit
breaker) · BackgroundService como cola · xUnit + WebApplicationFactory.
```
