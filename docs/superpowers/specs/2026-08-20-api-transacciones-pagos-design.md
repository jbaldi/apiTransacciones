# API de Transacciones de Pago — Diseño

**Fecha:** 2026-08-20
**Autor:** jose.baldi@gmail.com
**Estado:** Aprobado (brainstorming)

## 1. Objetivo

Construir una API REST en C# / .NET 10 que procese pagos contra un procesador
externo (un "tercero") de forma **resiliente y sin perder plata jamás**,
demostrando de forma tangible cuatro garantías:

1. **Idempotency-key end-to-end** (cliente↔API y API↔procesador).
2. **Timeout ≠ fallo** → estado `INCIERTO` (nunca asumir).
3. **Conciliación / reconciliation** como única fuente de verdad del dinero.
4. **Circuit breaker + ruteo a procesador alternativo** (OpenPass).

Bonus: patrón **Outbox** (persistir el evento en la misma transacción de BD y
publicarlo aparte) y **máquina de estados / Saga** para el ciclo de vida del
pago con compensaciones.

El proyecto es la base de una **presentación de LinkedIn** (explicación,
arquitectura, diseño y desarrollo). Debe compilar, correr y ser demostrable sin
infraestructura externa.

## 2. Alcance

- **Entregable:** presentación (artifact web estilo carrusel LinkedIn) **+**
  proyecto C# funcional.
- **Nivel de código:** funcional pero **simulado** — el procesador de pago es
  falso (in-memory) y la "cola" corre en memoria. Cero infra externa (sin
  Docker, sin RabbitMQ, sin Postgres).
- **Idioma:** todo en español (presentación y comentarios de código).

Fuera de alcance: autenticación real de usuarios, multi-moneda con conversión,
procesadores de pago reales, despliegue productivo.

## 3. Stack

| Pieza | Elección | Motivo |
|---|---|---|
| Runtime | .NET 10 Minimal API | Ya instalado; liviano. |
| Persistencia | SQLite + EF Core | BD transaccional real en un archivo → Outbox e idempotencia se demuestran de verdad (sobreviven a reinicios). |
| Cola / async | `System.Threading.Channels` + `BackgroundService` | La "cola" en memoria; worker lee el Outbox y despacha. |
| Resiliencia | Polly (timeout + retry backoff exponencial + circuit breaker) | Estándar de industria en .NET. |
| Procesadores | 2 implementaciones falsas (`PrimaryProcessor`, `AlternativeProcessor`=OpenPass) con "guiones" configurables | Permite forzar timeouts/fallos y disparar breaker+ruteo en la demo. |
| Conciliación | `GetStatus()` en el procesador falso + `ReconciliationWorker` | Fuente de verdad del dinero. |
| Tests | xUnit + `WebApplicationFactory` + SQLite in-memory + `FakeTimeProvider` | Integración realista sin esperas reales. |

**Idea clave:** el procesador falso acepta guiones (ej. *"timeout en intento 1,
PAID en el status"*) para grabar cada escenario de la demo.

## 4. Arquitectura

Tres momentos:

### Momento 1 — Recepción (síncrono, nunca perder el pago)
`POST /payments` con `Idempotency-Key` obligatoria. En **una sola transacción de
BD** se insertan `Payment(PENDIENTE)` + `OutboxMessage(PaymentRequested)`. Se
responde `202 recibido, en proceso`. No se habla con ningún externo acá. Misma
key repetida → se devuelve el resultado existente (no reprocesa).

### Momento 2 — Envío (asíncrono, no colgar al usuario)
`OutboxDispatcher` (BackgroundService) lee mensajes `PENDING` del Outbox y los
manda al `ProcessorRouter`. El router aplica la `ResiliencePipeline` de Polly
(timeout corto + retry con backoff exponencial + circuit breaker). Si el breaker
está abierto, rutea al `AlternativeProcessor` (OpenPass). Se reenvía la **misma
idempotency-key** al procesador. Resultado:
- OK → `PAGADO`
- Falla clara → `FALLIDO`
- Timeout → `INCIERTO` (no se asume nada)

### Momento 3 — Incertidumbre (asíncrono, el caso feo)
`ReconciliationWorker` (BackgroundService) toma los pagos `INCIERTO` y consulta
`GetStatus(processorRef)` en el procesador. **El estado del dinero lo define el
procesador, no la suposición.** `PAID` → `PAGADO`; `FAILED` → `FALLIDO`
(compensación); `UNKNOWN` → sigue INCIERTO y reintenta. Todo queda registrado en
un `EventLog` inmutable (append-only) para auditoría y replay.

### Componentes (una responsabilidad cada uno)
1. **Endpoints (`Api/`)** — reciben, validan idempotency-key, responden 202. No hablan con externos.
2. **`IdempotencyStore`** — "misma key → mismo resultado" vía constraint UNIQUE.
3. **`OutboxDispatcher`** — la "cola"; lee Outbox no despachado y despacha.
4. **`ProcessorRouter`** — Primary vs Alternative según el circuit breaker.
5. **`IPaymentProcessor`** (2 falsos) — `Charge()` y `GetStatus()`.
6. **`ResiliencePipeline` (Polly)** — timeout + retry backoff + breaker.
7. **`ReconciliationWorker`** — resuelve los INCIERTO consultando status.
8. **`EventLog`** — log inmutable append-only.
9. **`PaymentStateMachine`** — valida transiciones.

### Máquina de estados (Saga)
```
PENDIENTE ──▶ EN_PROCESO ──▶ PAGADO
    │              │
    │              ├──▶ FALLIDO
    │              └──▶ INCIERTO ──(conciliación)──▶ PAGADO
                          │                          │
                          └──────────────────────────┴──▶ FALLIDO (compensación)
```
Solo se permiten transiciones válidas. El dinero se da por cobrado únicamente
cuando la conciliación confirma.

### Estructura de carpetas
```
apiTransacciones/
├── src/ApiTransacciones/
│   ├── Domain/          (Payment, PaymentState, PaymentStateMachine, eventos)
│   ├── Persistence/     (DbContext, IdempotencyStore, OutboxStore, EventLog)
│   ├── Processors/      (IPaymentProcessor, Primary, Alternative, guiones)
│   ├── Resilience/      (Polly pipeline + ProcessorRouter)
│   ├── Workers/         (OutboxDispatcher, ReconciliationWorker)
│   ├── Api/             (endpoints, DTOs)
│   └── Program.cs
└── tests/ApiTransacciones.Tests/
```

## 5. Modelo de datos (SQLite / EF Core)

### `Payments`
| Campo | Tipo | Notas |
|---|---|---|
| `Id` | GUID (PK) | id interno |
| `IdempotencyKey` | string | **UNIQUE** — corazón de la idempotencia |
| `Amount` | decimal | monto |
| `Currency` | string(3) | ej. ARS |
| `State` | string(enum) | PENDIENTE/EN_PROCESO/PAGADO/FALLIDO/INCIERTO |
| `ProcessorUsed` | string? | Primary/Alternative |
| `ProcessorRef` | string? | id de la operación en el procesador (para conciliar) |
| `Attempts` | int | reintentos |
| `CreatedAt`/`UpdatedAt` | datetime | auditoría |
| `ResponseSnapshot` | string(json)? | resultado cacheado para misma key |

### `OutboxMessages`
| Campo | Tipo | Notas |
|---|---|---|
| `Id` | GUID (PK) | |
| `PaymentId` | GUID (FK) | |
| `Type` | string | ej. PaymentRequested |
| `Payload` | string(json) | |
| `Status` | string | PENDING/DISPATCHED/FAILED |
| `CreatedAt`/`DispatchedAt` | datetime | |
| `RetryCount` | int | |

**Outbox atómico:** `Payment(PENDIENTE)` + `OutboxMessage` en la misma
transacción. Si la app se cae tras el 202, el mensaje no se pierde.

### `EventLog` (inmutable, append-only)
| Campo | Tipo | Notas |
|---|---|---|
| `Id` | long (PK autoinc) | orden garantizado |
| `PaymentId` | GUID | |
| `EventType` | string | PaymentReceived, SentToProcessor, ProcessorTimeout, MarkedUncertain, ReconciliationConfirmed, StateChanged… |
| `Data` | string(json) | |
| `OccurredAt` | datetime | |

Solo INSERT — nunca UPDATE/DELETE.

## 6. Contratos de API

### Crear pago (Recepción)
```
POST /payments
Header: Idempotency-Key: <uuid>   (obligatorio)
Body:   { "amount": 1500.00, "currency": "ARS", "customerId": "..." }

202 Accepted → { "paymentId", "state": "PENDIENTE", "message": "recibido, en proceso" }
Misma key    → 200 OK con el ResponseSnapshot existente (no reprocesa)
Sin header   → 400 { "error": "Idempotency-Key requerida" }
```

### Consultar estado
```
GET /payments/{id}
200 → { "paymentId", "state", "processorUsed", "attempts" }
```

### Historial de eventos (auditoría)
```
GET /payments/{id}/events
200 → [ { eventType, occurredAt, data }, ... ]
```

### Control de la demo
```
POST /demo/processor-behavior
{ "processor": "primary", "mode": "timeout|fail|ok|slow", "failCount": 3 }
→ configura el guion del procesador falso para forzar cada escenario.
```

Procesador falso (interno):
```
Charge(idempotencyKey, amount) → OK(ref) | Fail(reason) | Timeout(throw)
GetStatus(processorRef)        → PAID | FAILED | UNKNOWN   ← fuente de verdad
```

## 7. Estrategia de testing (TDD)

Tests primero; uno por garantía. xUnit + `WebApplicationFactory` + SQLite
in-memory + `FakeTimeProvider`.

| # | Test | Garantiza |
|---|---|---|
| 1 | `MismaKey_DevuelveMismoResultado_SinReprocesar` | Idempotencia e2e |
| 2 | `SinIdempotencyKey_Retorna400` | Validación recepción |
| 3 | `Payment_y_Outbox_SeInsertan_EnLaMismaTransaccion` | Outbox atómico |
| 4 | `SiFallaAntesDeDespachar_ElMensajeSobrevive` | Durabilidad Outbox |
| 5 | `ProcesadorTimeout_DejaEstadoINCIERTO_NoFALLIDO` | Timeout ≠ fallo |
| 6 | `Breaker_SeAbre_TrasNFallos_YRuteaAlAlternativo` | Circuit breaker + ruteo |
| 7 | `Reintentos_UsanBackoffExponencial` | Backoff |
| 8 | `Conciliacion_DefineElDinero_StatusPAID_MarcaPAGADO` | Reconciliation = verdad |
| 9 | `Conciliacion_StatusFAILED_CompensaAFallido` | Saga compensación |
| 10 | `EventLog_EsInmutable_SoloAppend` | Auditoría |
| 11 | `TransicionInvalida_EsRechazada` | Máquina de estados |

## 8. Entregable de presentación (LinkedIn)

Artifact web (una página, estilo carrusel/slides) que cubre:
1. El problema (los 3 momentos).
2. Los 4 conceptos obligatorios + los 2 bonus.
3. Diagrama de arquitectura.
4. Máquina de estados.
5. Snippets del código real del proyecto.
6. Los escenarios de demo y qué garantiza cada uno.

## 9. Criterios de éxito

- `dotnet build` y `dotnet test` pasan (los 11 tests en verde).
- La API corre y responde 202 en recepción.
- Se puede forzar, vía `/demo/processor-behavior`, cada escenario: idempotencia,
  timeout→INCIERTO, breaker→ruteo, conciliación→PAGADO/FALLIDO.
- El artifact de LinkedIn renderiza y es autoexplicativo.
