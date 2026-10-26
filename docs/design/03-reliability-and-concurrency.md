# 03 — Concurrency & Reliability

## 1. Concurrency model (Processor)

```
POST /api/process ──TryWrite──► Channel<ProcessingJob>(capacity: QueueCapacity=100)
                                   │  full → 503 + Retry-After
                                   ▼
                   N = MaxConcurrentJobs (10) long-running worker loops
                     (await channel.Reader.ReadAsync — no thread is blocked while idle)
                                   │
                                   ▼  per job
                   Parallel.ForEachAsync(documents, MaxDegreeOfParallelism = DocParallelismPerJob (2))
                     → DocumentAnalyzer.Analyze(doc)   (pure, CPU-bound, per-doc try/catch)
```

**Why this model**
- **Two explicit bounds:** at most 10 jobs × 2 documents = **20 CPU work items** at once, and at most 100 jobs waiting. Nothing is unbounded: no `Task.Run` per document and no unbounded queue.
- `Channel<T>` gives async producer/consumer with backpressure built in. Workers `await` on it, so idle workers hold no threads.
- **Fairness:** every running job makes progress. A 1,000-document job does not starve a 5-document job, because each job gets its own small slice of parallelism.
- Everything is async except the CPU-bound analysis itself, which runs on thread-pool threads through `Parallel.ForEachAsync`. Nothing calls `.Result` or `.Wait()`.

**Trade-offs and limits**
- 10 × 2 can oversubscribe a 4-core box slightly. That is acceptable for short CPU bursts, and both numbers are configurable. A global CPU limiter (`SemaphoreSlim(ProcessorCount)`) would cap CPU work exactly, but it would add contention and hurt per-job fairness.
- Work queued in the processor lives in memory. A restart loses it, and the Job Service reconciler recovers it (see §3).
- A single processor instance. Scaling out works naturally (the dispatcher only needs a load-balanced URL), but the WS fan-in and the SSE broadcaster are single-instance today.

**Job Service side:** the dispatcher sends up to `MaxParallelDispatches` (default 4) HTTP calls at once. The WS endpoint handles one connection per processor and processes events **sequentially per connection**, which preserves order from a single sender. Mongo writes are async.

## 2. Idempotency and state integrity

| Mechanism | Protects against |
|---|---|
| State machine and conditional Mongo update (`status ∈ allowedFrom`) | Duplicate or out-of-order events, races between the WS handler, reconciler and cancel |
| `processedDocuments = max(...)` | Out-of-order progress |
| Results upsert keyed by `(jobId, index)` | Replayed `JobCompleted` |
| `POST /api/process` idempotent by `jobId` | Dispatcher retry after a timeout when the processor did accept the job |
| `POST /api/audit` upsert by `jobId` | Audit publisher retries |
| Dispatcher claim lease (`leaseUntil`) | Two dispatch loops sending the same job |

## 3. Failure-mode catalogue

| # | Scenario | Behaviour |
|---|---|---|
| 1 | **Invalid input** (no documents, >1000, content >100 KB, null content, malformed JSON) | 400 ProblemDetails listing field errors. Body >10 MB returns 413. Nothing is persisted. |
| 2 | **Processor unreachable** (connection refused, timeout, 5xx) | Job stays `Pending`. Retries use exponential backoff + jitter (2s, 4s, 8s… cap 60s). After 5 attempts: `Failed("Processor unreachable")`. |
| 3 | **Processor overloaded** (503 queue full) | Retried after `Retry-After`. **Does not consume** an attempt; it is backpressure, not a failure. |
| 4 | **Processor rejects the payload** (400) | `Failed` immediately (non-retryable) and logged as an error, because it means a contract bug. |
| 5 | **WS disconnected** | Processor reconnects with backoff (1s → 30s). Lifecycle events wait in a bounded outbox (10k) and are resent on reconnect until acked. Progress events are dropped while disconnected. |
| 6 | **Duplicate / out-of-order events** | Absorbed by §2 and acked anyway. |
| 7 | **Event lost** (e.g. outbox overflow, processor crash after work finished) | The reconciler sees a stale `Queued`/`Processing` job (60s without updates) and pulls the state with `GET /api/process/{id}`. |
| 8 | **Processor restart mid-job** | Reconciler gets 404 → back to `Pending` → redispatch. This is safe because the analysis is deterministic and idempotent. After 3 redispatches: `Failed("Processor lost job repeatedly")`. |
| 9 | **Single document throws** | That document is `Failed` with an error and the others continue. The job is `Completed` with `summary.failed > 0`. The job is `Failed` only if **every** document failed. |
| 10 | **Cancel during processing** | Job → `Cancelled` right away (the user sees it). A best-effort `DELETE /api/process/{id}` is sent. A late `JobCompleted` is ignored by the state machine. |
| 11 | **Delete an active job** | 409, because deleting would orphan in-flight work. Cancel it first. |
| 12 | **Mongo down** | Readiness reports 503. API calls return 503 ProblemDetails. WS events are **not acked**, so the processor keeps them and resends them. Background loops log and retry on the next tick. |
| 13 | **Audit service down** | Job is unaffected. The `auditPublished=false` flag on the job acts as an outbox, and the publisher retries with backoff. |
| 14 | **Job Service restart** | All state is in Mongo. The dispatcher picks up `Pending` jobs, the reconciler covers `Queued`/`Processing`, and the processor reconnects its WS and replays its outbox. |

## 4. What we deliberately do **not** guarantee
- Exactly-once delivery. We provide at-least-once delivery with idempotent handlers, which gives effectively-once state changes.
- A durable outbox in the processor. It is in memory; the reconciler is the safety net.
- Cross-instance SSE or WS fan-in. Production would put a broker (Redis pub/sub, RabbitMQ or Kafka) behind them.

## 5. Observability
- **Serilog** writes compact JSON to the console. Each log line is enriched with `Service`, `CorrelationId` and `JobId` (log scope inside every job-related operation).
- The correlation id flows client → Job Service → (HTTP header) Processor → (WS envelope) back to the Job Service → Audit.
- Key events are logged at Information level: job created, dispatched, state transitions, retries (Warning), giving up (Error), WS connect/disconnect.
- Health checks drive the docker-compose `healthcheck` / `depends_on: condition: service_healthy`.
