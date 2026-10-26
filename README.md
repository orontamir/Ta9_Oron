# TA9 — Real-Time Document Processing System

Three .NET 8 services that process documents asynchronously and talk over **REST**, **WebSocket** and **SSE**:

| Service | Port | Role |
|---|---|---|
| **Job Service** | 5001 | Public API (`/api/jobs`). Owns the job lifecycle (source of truth, MongoDB), dispatches work, consumes processor events over WebSocket, streams status to clients over SSE |
| **Processor Service** | 5002 | Internal API (`/api/process`). Analyzes documents with bounded concurrency and reports the lifecycle back over a WebSocket |
| **Audit Service** (bonus) | 5003 | Stores immutable summaries of finished jobs (`/api/audit`) |

For each document the processor computes the word count, unique word count, top-5 words, SHA-256, character count, line count, average word length and longest word.

> **Installation Guide** (prerequisites, local and Docker Desktop deployment, errors encountered and fixes): [`docs/installation/INSTALLATION.md`](docs/installation/INSTALLATION.md) · Word: [`TA9-Installation-Guide.docx`](docs/installation/TA9-Installation-Guide.docx).
> **High-Level Design** with Architecture, Class, Flow, State and Sequence diagrams: [`docs/hld/HLD.md`](docs/hld/HLD.md) (editable draw.io source: [`docs/hld/diagrams/ta9-hld.drawio`](docs/hld/diagrams/ta9-hld.drawio)) · Word: [`docs/hld/TA9-HLD.docx`](docs/hld/TA9-HLD.docx).
> Detailed design documents, the full decision log and the failure catalogue are in [`docs/design`](docs/design).

---

## 1. Build and run

### Docker (recommended)
```bash
docker compose up --build -d
docker compose ps            # wait until all four containers are "healthy"
```
- Swagger: http://localhost:5001/swagger · http://localhost:5002/swagger · http://localhost:5003/swagger
- Postman: import [`docs/postman/Ta9.postman_collection.json`](docs/postman/Ta9.postman_collection.json) and run the requests in order. "Create job" stores `{{jobId}}` for the rest.
- Smoke test: `./scripts/smoke-test.ps1` (or `./scripts/smoke-test.sh`). The assignment's load scenario: `./scripts/smoke-test.ps1 -Jobs 10 -Documents 1000`
- Live status stream: `curl -N http://localhost:5001/api/jobs/<id>/events`

> **Behind a TLS-inspecting corporate proxy**, the build fails with `NU1301` because `dotnet restore` inside the container does not trust the proxy's certificate. Use `./scripts/docker-up-prebuilt.ps1` instead. It publishes on the host and lets Docker only package the binaries (`Dockerfile.prebuilt`). If PowerShell refuses unsigned scripts from a network share, run `powershell -ExecutionPolicy Bypass -File .\scripts\<script>.ps1`.

### Locally with the .NET 8 SDK
```bash
docker run -d -p 27017:27017 mongo:7.0                    # or set Storage__Provider=InMemory (Job + Audit)
dotnet run --project src/Ta9.JobService                   # http://localhost:5001
dotnet run --project src/Ta9.ProcessorService             # http://localhost:5002
dotnet run --project src/Ta9.AuditService                 # http://localhost:5003
```
All settings live in each service's `appsettings.json` and can be overridden with environment variables (e.g. `Processing__MaxConcurrentJobs=20`).

## 2. Tests
```bash
dotnet test Ta9.sln                                   # everything (~400 tests); Docker must be running
dotnet test Ta9.sln --filter "Category!=Integration"  # without Docker: unit + in-memory API tests
```

| Project | What it covers |
|---|---|
| `Ta9.ServiceDefaults.Tests` | Correlation id (validation, async flow, middleware, HttpClient propagation), backoff |
| `Ta9.ProcessorService.Tests` | Tokenizer and analyzer rules; state, throttling, runner, worker pool **concurrency bounds**; queue/backpressure; REST API; outbox semantics; **real WebSocket** client tests (replay, buffering, ack timeout) against a fake Job Service |
| `Ta9.JobService.Tests` | Exhaustive state-transition table; aggregate rules; optimistic concurrency; one **repository contract suite run against both InMemory and real MongoDB** (Testcontainers); dispatcher, reconciler, event handler, audit publisher (manual clock); REST, WebSocket endpoint and SSE through the real pipeline |
| `Ta9.AuditService.Tests` | API, validation, repository contract (InMemory + MongoDB) |
| `Ta9.IntegrationTests` | All three services in-process, wired over REST + a real WebSocket + MongoDB: full flow, **10 jobs × 1,000 documents** with bounded parallelism asserted, unreachable processor, **processor restart mid-job**, cancellation |

## 3. Architecture

```
Client ──REST /api/jobs──► Job Service ──REST POST /api/process──► Processor Service
   ▲                         │   ▲                                   │
   └──SSE /api/jobs/id/events┘   └──── WebSocket events + acks ──────┘
                             │
                             ├──► MongoDB (jobs, job_documents)
                             └──REST POST /api/audit──► Audit Service ──► MongoDB (audit_entries)
```

**Job lifecycle:** `Pending → Queued → Processing → Completed | Failed`, plus `Cancelled`. `Queued` means the processor accepted the job but has not started it. All transitions are defined in one `JobStateMachine` and enforced by the `Job` aggregate.

**Main flow:**
1. `POST /api/jobs` validates the request and stores the documents, then the job (`Pending`), and returns **201** right away.
2. The **dispatcher** atomically claims the job with a lease and sends it to the processor. On 202 the job becomes `Queued`.
3. The processor queues the job, analyzes its documents and sends `JobStarted`, throttled `JobProgress` and `JobCompleted` (with all results) over its WebSocket connection.
4. The Job Service applies each event idempotently, stores the results **before** setting `Completed`, and acks.
5. Every job change is pushed to SSE subscribers. Finished jobs are published to the Audit Service.

**Code layout** (one project per service, with layered folders):
```
src/Ta9.Contracts         shared REST/WS DTOs, limits, event serializer
src/Ta9.ServiceDefaults   Serilog, correlation id, ProblemDetails, health checks, backoff
src/Ta9.<Service>/        Api/ · Application/ · Domain/ · Infrastructure/
tests/…                   one test project per service + cross-service integration tests
```

## 4. Key technical decisions and trade-offs
Full log with the alternatives considered: [`docs/design/04-decisions.md`](docs/design/04-decisions.md).

| Decision | Why | Trade-off |
|---|---|---|
| Background **dispatcher**; POST returns immediately | The client does not depend on processor availability; work survives restarts | Eventual consistency: the client polls or subscribes |
| Retry state **persisted in the job** (`dispatchAttempts`, `nextDispatchAt`, `leaseUntil`) + atomic claim | Retries survive restarts and never block threads. Several dispatchers never double-dispatch | More fields on the job record |
| **Optimistic concurrency** (`Version`) with all rules in the domain | Concurrent writers (WS handler, dispatcher, reconciler, cancel) cannot overwrite each other. The rules are unit-testable without a DB | Retries on contention |
| Results inside `JobCompleted` (~0.5 MB for 1,000 docs) | One hop on the happy path | Large WS message (16 MB cap) |
| A failing document does not fail the job | Do not throw away 999 good results | The client must check per-document status |
| MongoDB: `jobs` + `job_documents` | Avoids the 16 MB document limit; status polling stays cheap | No multi-document transactions; write order makes this safe (D21) |
| Storage behind interfaces; `Storage:Provider = Mongo \| InMemory` | Swapping storage is a configuration change | Two implementations to keep aligned (one shared contract test suite) |
| SSE for clients, WebSocket between services | SSE is one-way, plain HTTP and auto-reconnects. The service link needs acks back | — |
| GUIDs stored as strings in Mongo | Driver 2.x legacy GUID mode broke equality filters (caught by the contract tests) | Slightly larger documents |

## 5. Concurrency and performance
```
POST /api/process ─► bounded Channel (100) ─► 10 worker loops ─► Parallel.ForEachAsync(docs, MaxDegreeOfParallelism = 2)
                       full → 503 + Retry-After
```
- **Two explicit bounds:** at most 10 jobs × 2 documents = **20 CPU work items** at once, and at most 100 jobs waiting. Nothing is unbounded: no task per document and no unbounded queue.
- **No blocked threads:** idle workers `await` the channel, and all I/O is async. Only the CPU-bound analysis occupies thread-pool threads.
- **Fairness:** every running job progresses, so one 1,000-document job cannot starve small jobs.
- **Backpressure end to end:** a full queue returns 503 with `Retry-After`. The dispatcher waits for the hint **without** counting a failed attempt.
- **Verified:** the integration test runs 10 concurrent jobs × 1,000 documents and asserts every document is analyzed exactly once with **≤ 20** concurrent analyses. `scripts/smoke-test.ps1 -Jobs 10 -Documents 1000` runs the same scenario against Docker.
- **Limits:** 10 × 2 can slightly oversubscribe a small machine; all numbers are configurable (`Processing__*`). The queue is in memory, and the reconciler recovers it after a processor restart.

## 6. WebSocket disconnect / reconnect behavior
- The **processor is the client** and connects to `ws://job-service/ws/processor-events`. It reconnects with exponential backoff from 1s to 30s with ±20% jitter.
- **Lifecycle events** (started, completed, failed, cancelled) sit in a bounded **outbox** (10k) until the Job Service **acks** them. They are resent after every reconnect and when no ack arrives within 30s. Delivery is **at-least-once**.
- **Progress events** are best-effort: coalesced to the latest per job, dropped while disconnected, never replayed.
- **Duplicates and out-of-order events** are harmless. The state machine rejects illegal transitions, progress only moves forward (`max`), and result writes are idempotent. Ignored events are still acked. Storage failures are **not** acked, so the processor resends them.
- While disconnected the processor keeps working and `/health/ready` reports **Degraded** (HTTP 200).
- **Safety net:** a **reconciler** checks jobs that have been silent for 60s through `GET /api/process/{id}`. It applies the processor's status. If the processor no longer knows the job (it restarted), the job goes back to `Pending` and is redispatched, up to 3 times.

## 7. Reliability and edge cases
Full catalogue: [`docs/design/03-reliability-and-concurrency.md`](docs/design/03-reliability-and-concurrency.md).

| Scenario | Behavior |
|---|---|
| Invalid input | 400 ValidationProblemDetails with field errors (1–1,000 docs, ≤ 100 KB UTF-8 each, ≤ 10 MB body). Empty content is valid (0 words) |
| Processor unreachable | Job stays `Pending`; exponential backoff; `Failed` after 5 attempts with the reason |
| Processor overloaded | 503 + `Retry-After`; retried later without counting as a failure |
| Processor restart mid-job | The reconciler detects the lost job and redispatches it, which is safe because analysis is deterministic |
| Small job finishes before the dispatcher records the 202 | Events for a `Pending` job first mark it `Queued` |
| Cancel | Immediate `Cancelled`; propagated to the processor; a late completion is ignored |
| Delete active job | 409: cancel first |
| MongoDB down | 503 ProblemDetails (5s fail-fast); readiness 503; events not acked |
| Audit Service down | Jobs unaffected; the `auditPublished` flag is an outbox, retried with backoff |

## 8. Observability
- **Serilog** structured logs: JSON in containers, readable text in Development. Every line carries `Service`, `CorrelationId` and `JobId`.
- **Correlation id** (`X-Correlation-Id`) flows client → Job Service → processor (HTTP header) → back in WS events → audit. One id lets you trace a job across all services.
- **Health:** `/health/live` and `/health/ready` (storage, processor WS link). Used by the docker-compose health checks.

## 9. Known limitations and what I would improve for production
- **Single instance for the in-process fan-out:** the SSE broadcaster and the WS fan-in are per instance. With several Job Service instances, publish job changes through Redis pub/sub or a broker.
- **Processor state is in memory:** queued work and its outbox are lost on restart. The reconciler recovers it, at the cost of up to about 60s of latency. A broker (RabbitMQ or Kafka) with durable queues would replace the REST dispatch, the WS events and the reconciler.
- **No authentication/authorization** (out of scope). Production would use JWT for the public API and mTLS or a shared secret for the service-to-service WS and REST.
- **No rate limiting or tenant quotas** on the public API.
- **Metrics/tracing:** add OpenTelemetry (traces across the HTTP/WS hops; metrics for queue depth, dispatch latency, outbox size).
- Swagger is enabled in every environment for this POC.
- A job deleted before its audit summary is published is never audited.
- Very long processor outages: jobs stay `Queued`/`Processing` and are only rechecked. A maximum age before failing could be added.
- The prebuilt Docker path exists only because of a TLS-inspecting network. The standard multi-stage build is the intended one.

## 10. API summary
| Method | Path | Notes |
|---|---|---|
| POST | `/api/jobs` | `{ name?, documents: [{ name, content }] }` → 201 |
| GET | `/api/jobs?status=&page=&pageSize=` | Newest first |
| GET | `/api/jobs/{id}` | Status, progress %, summary, timestamps, failure reason |
| GET | `/api/jobs/{id}/results?page=&pageSize=` | Per-document results; 409 until Completed |
| GET | `/api/jobs/{id}/events` | SSE stream |
| POST | `/api/jobs/{id}/cancel` | Idempotent; 409 if already finished |
| DELETE | `/api/jobs/{id}` | Finished jobs only (409 otherwise) |
| POST/GET/DELETE | `/api/process`, `/api/process/{id}` | Processor (internal); POST is idempotent by `jobId` |
| WS | `/ws/processor-events` | Processor → Job Service events, Job Service → processor acks |
| POST/GET | `/api/audit`, `/api/audit/{jobId}` | Audit (first write wins) |

Full contracts: [`docs/design/02-api-and-contracts.md`](docs/design/02-api-and-contracts.md).

---

## 11. AI Usage

**Tools:** Claude Code (Anthropic, Claude Opus 5.5) in a terminal session.

**What it was used for**
- Turning the assignment into a design. The AI listed the grey areas as multiple-choice questions with a recommendation for each; **I made every decision** before any code was written (recorded in `docs/design/04-decisions.md`).
- Writing the design documents, then the code and tests **step by step** (13 planned steps, each ending with green tests and its own commits), with an explanation of every step so I could review it and explain it.
- Diagnosing failures. The code was written in an environment without the .NET SDK, so **I built and ran every step on my machine** and fed the failures back.

**AI-generated or AI-assisted parts:** essentially all source code, tests, Docker/compose files, scripts and documentation were AI-generated from my decisions and reviewed by me step by step. The architecture choices, the scope (C#/.NET 8, MongoDB, Docker Compose, the Audit bonus) and the trade-offs were mine.

**Representative prompts** (translated from Hebrew)
- "We'll write the solution in C# .NET 8, with Docker Compose, include the Audit service, and use MongoDB running as a Docker container. Some parts of the task are grey areas: in your plan, ask me the questions and I'll decide before we write code. Write the code step by step so I can explain to the interviewer what I did, with unit tests for every step, and design documents."
- "11 tests failed: [pasted test output]" (the Mongo GUID issue below)
- "[pasted docker build output]" → diagnosis of TLS inspection and the prebuilt packaging path

**Examples of AI suggestions I changed, rejected or that were corrected**
1. **Language and stack.** The AI recommended Python + FastAPI because it was the toolchain available in its environment. I rejected that and chose C#/.NET 8, the stack I want to defend in the interview, plus MongoDB as the storage (the AI had not proposed a database).
2. **MongoDB GUID mapping bug.** The first Mongo repository registered a standard binary GUID serializer. Running the contract suite against a real MongoDB showed 11 failures: every query filtered by id returned nothing, while unfiltered reads worked. The fix stores GUIDs as strings with explicit per-member serializers (decision D22). The InMemory implementation could never have exposed this, which is why the same contract tests run against both stores.
3. **`[Produces("application/json")]` on a controller** overrode the content type of error responses, so 404s were returned as `application/json` instead of RFC 7807 `application/problem+json`. A test caught it and the attribute was removed.
4. **Retry mechanism.** The AI's initial plan used in-process Polly retries for dispatch. When implementing the dispatcher it proposed replacing them with retry state persisted in the job plus an atomic lease, and I approved (D24): retries now survive restarts and work with several dispatchers.
5. **MVC implicit `[Required]`** on non-nullable strings rejected empty document content, which is valid by design, and would have made the processor reject whole jobs. It was disabled in all services so the validators are the single source of input rules (D23).
6. **Docker build behind TLS inspection.** The multi-stage build failed in my network (`NU1301`). Instead of baking a corporate CA certificate into the image or repo, I kept the standard Dockerfile and added a host-publish + package-only path.
