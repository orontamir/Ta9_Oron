# TA9 — Real-Time Document Processing System

Three .NET 8 services that process documents asynchronously and talk over **REST**, **WebSocket** and **SSE**:

| Service | Port | Role |
|---|---|---|
| **Job Service** | 5001 | Public API (`/api/jobs`). Owns the job lifecycle (source of truth, MongoDB), dispatches work, consumes processor events over WebSocket, streams status to clients over SSE |
| **Processor Service** | 5002 | Internal API (`/api/process`). Analyzes documents with bounded concurrency and reports the lifecycle back over a WebSocket |
| **Audit Service** (bonus) | 5003 | Stores immutable summaries of finished jobs (`/api/audit`) |
| **Web UI** (optional) | 4200 | Angular 17 console over the same REST API: list, create (text, `.txt` files, or up to 1,000 generated documents), live progress (SSE), results, cancel / delete, audit entry. Served by nginx, which proxies `/api` to the services |

For each document the processor computes the word count, unique word count, top-5 words, SHA-256, character count, line count, average word length and longest word.

> **WebSocket disconnect/reconnect behavior** (outbox, acks, backoff, idempotency, reconciler, tests): [`docs/websocket/WEBSOCKET.md`](docs/websocket/WEBSOCKET.md) · Word: [`TA9-WebSocket-Behavior.docx`](docs/websocket/TA9-WebSocket-Behavior.docx).
> **Concurrency & Performance** (chosen strategy, advantages, limitations, diagrams): [`docs/concurrency/CONCURRENCY.md`](docs/concurrency/CONCURRENCY.md) · Word: [`TA9-Concurrency-Performance.docx`](docs/concurrency/TA9-Concurrency-Performance.docx).
> **Storage** (interfaces, InMemory + MongoDB, data model, outage handling, how to replace it, tests): [`docs/storage/STORAGE.md`](docs/storage/STORAGE.md) · Word: [`TA9-Storage.docx`](docs/storage/TA9-Storage.docx).
> **User Guide** (how to use the system: Web UI, Swagger or Postman, step by step with screenshots): [`docs/user-guide/USER-GUIDE.md`](docs/user-guide/USER-GUIDE.md) · Word: [`TA9-User-Guide.docx`](docs/user-guide/TA9-User-Guide.docx).
> **AI usage disclosure** (tools, workflow, prompts, changed/rejected AI suggestions): [`docs/ai-usage/AI-USAGE.md`](docs/ai-usage/AI-USAGE.md) · Word: [`TA9-AI-Usage.docx`](docs/ai-usage/TA9-AI-Usage.docx).
> **Known limitations & production improvements** (by area, roadmap, production architecture diagram): [`docs/limitations/LIMITATIONS.md`](docs/limitations/LIMITATIONS.md) · Word: [`TA9-Limitations-Production.docx`](docs/limitations/TA9-Limitations-Production.docx).
> **Logging & Observability** (console + MongoDB, levels, correlation, tests): [`docs/logging/LOGGING.md`](docs/logging/LOGGING.md) · Word: [`TA9-Logging-Observability.docx`](docs/logging/TA9-Logging-Observability.docx). **Something does not start, hangs or crashes?** See the Installation Guide §5 *Troubleshooting — where to look*.
> **Software Test Description (STD)**: test strategy, environment, traceability to the requirements, all 311 automated test cases (440 executions), manual procedures and defects: [`docs/testing/STD.md`](docs/testing/STD.md) · Word: [`TA9-STD.docx`](docs/testing/TA9-STD.docx).
> **Installation Guide** (prerequisites, local and Docker Desktop deployment, troubleshooting, errors encountered and fixes): [`docs/installation/INSTALLATION.md`](docs/installation/INSTALLATION.md) · Word: [`TA9-Installation-Guide.docx`](docs/installation/TA9-Installation-Guide.docx).
> **High-Level Design** with Architecture, Class, Flow, State and Sequence diagrams: [`docs/hld/HLD.md`](docs/hld/HLD.md) (editable draw.io source: [`docs/hld/diagrams/ta9-hld.drawio`](docs/hld/diagrams/ta9-hld.drawio)) · Word: [`docs/hld/TA9-HLD.docx`](docs/hld/TA9-HLD.docx).
> Detailed design documents, the full decision log and the failure catalogue are in [`docs/design`](docs/design).

---

## 1. Build and run

### Docker (recommended)
```bash
docker compose up --build -d
docker compose ps            # wait until all five containers are "healthy"
```
- **Web UI: http://localhost:4200**
- Swagger: http://localhost:5001/swagger · http://localhost:5002/swagger · http://localhost:5003/swagger
- Postman: import [`docs/postman/Ta9.postman_collection.json`](docs/postman/Ta9.postman_collection.json) and run the requests in order. "Create job" stores `{{jobId}}` for the rest.
- Smoke test: `./scripts/smoke-test.ps1` (or `./scripts/smoke-test.sh`). The assignment's load scenario: `./scripts/smoke-test.ps1 -Jobs 10 -Documents 1000`
- Live status stream: `curl -N http://localhost:5001/api/jobs/<id>/events`

> **Behind a TLS-inspecting corporate proxy**, the build fails with `NU1301` because `dotnet restore` inside the container does not trust the proxy's certificate. Use `./scripts/docker-up-prebuilt.ps1` instead. It publishes on the host and lets Docker only package the binaries (`Dockerfile.prebuilt`). The Web UI then uses its committed build (`ui/prebuilt`), so npm is not needed either (some networks also block npm downloads). If PowerShell refuses unsigned scripts from a network share, run `powershell -ExecutionPolicy Bypass -File .\scripts\<script>.ps1`.

### Locally with the .NET 8 SDK
```bash
docker run -d -p 27017:27017 mongo:7.0                    # or Storage__Provider=InMemory (Job + Audit) and Logging__Mongo__Enabled=false
dotnet run --project src/Ta9.JobService                   # http://localhost:5001
dotnet run --project src/Ta9.ProcessorService             # http://localhost:5002
dotnet run --project src/Ta9.AuditService                 # http://localhost:5003
```
Web UI against the local services: `cd ui && npm ci && npm start` → http://localhost:4200 (the dev-server proxy forwards `/api` to ports 5001 / 5003). Node.js 20–24.

All settings live in each service's `appsettings.json` and can be overridden with environment variables (e.g. `Processing__MaxConcurrentJobs=20`).

## 2. Tests
```bash
dotnet test Ta9.sln                                   # everything (~440 tests); Docker must be running
dotnet test Ta9.sln --filter "Category!=Integration"  # without Docker: unit + in-memory API tests
cd ui && npm ci && npm run test:ci                    # Web UI: 21 component tests (headless Chrome)
```

| Project | What it covers |
|---|---|
| `Ta9.ServiceDefaults.Tests` | Correlation id (validation, async flow, middleware, HttpClient propagation), backoff; **logging pipeline**: batched MongoDB sink (non-blocking, drops counted, flush on stop), record mapping, levels and correlation through the real host, MongoDB writer with TTL (Testcontainers) |
| `Ta9.ProcessorService.Tests` | Tokenizer and analyzer rules; state, throttling, runner, worker pool **concurrency bounds**; queue/backpressure; REST API; outbox semantics; **real WebSocket** client tests (replay, buffering, ack timeout) against a fake Job Service |
| `Ta9.JobService.Tests` | Exhaustive state-transition table; aggregate rules; optimistic concurrency; one **repository contract suite run against both InMemory and real MongoDB** (Testcontainers); dispatcher, reconciler, event handler, audit publisher (manual clock); REST, WebSocket endpoint and SSE through the real pipeline |
| `Ta9.AuditService.Tests` | API, validation, repository contract (InMemory + MongoDB) |
| `ui` (Jasmine + Karma) | Web UI: API client (URLs, parameters, 404 → "not recorded yet"), error messages from ProblemDetails, jobs list, create form (validation hints, sample generator, server errors), job details (live updates, results, audit, actions) |
| `Ta9.IntegrationTests` | All three services in-process, wired over REST + a real WebSocket + MongoDB: full flow, **10 jobs × 1,000 documents** with bounded parallelism asserted, unreachable processor, **processor restart mid-job**, cancellation |

## 3. Architecture

```
Browser ──► Web UI :4200 (nginx) ──/api/jobs, /api/audit──┐   (optional console, same public API)
                                                          ▼
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
ui/                       Angular 17 web console (+ nginx.conf, Dockerfile, prebuilt/ for restricted networks)
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
| Logs: console ≥ Information + **own batched MongoDB sink** ≥ Debug | The flow stays readable on the console, the detail needed after a failure is searchable in one place for all services. Logging never blocks or crashes a service | Records can be dropped when MongoDB is down or under an extreme burst (counted and reported; the console has everything) |
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
Full description with state diagrams, alternatives, limitations and tests: [`docs/websocket/WEBSOCKET.md`](docs/websocket/WEBSOCKET.md).

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
Full description: [`docs/logging/LOGGING.md`](docs/logging/LOGGING.md). Where to look when something fails: Installation Guide §5.

- **Two destinations, one pipeline** (`Ta9.ServiceDefaults`, identical in all services):
  - **Console** (`docker compose logs`): Information and up, JSON in containers. The live flow and every warning and error.
  - **MongoDB `ta9_logs.logs`**: Debug and up, from all three services, kept 7 days (TTL index). Written by our own batched sink: a bounded buffer that never blocks, batches of 100 / 1 s, flushed on shutdown and on crash, losses counted and reported.
- **Levels:** Debug = detail to investigate (claims, HTTP results, progress, acks) · Information = the job flow and one line per HTTP request · Warning = handled problems (retries, one failed document, reconnects) · Error = failed operations with the stack trace · Critical = unhandled exception.
- **Correlation id** (`X-Correlation-Id`) is stored with the job and flows client → Job Service → processor (HTTP header) → back in WS events → audit. With `JobId` on every line, `db.logs.find({ jobId: "<id>" })` shows a job's whole path.
- **Health:** `/health/live` and `/health/ready` (storage, processor WS link). Used by the docker-compose health checks.

## 9. Known limitations and what I would improve for production
Full list (45 items with IDs, impact, today's mitigation and the production fix), a prioritized roadmap and a today-vs-proposed architecture diagram: [`docs/limitations/LIMITATIONS.md`](docs/limitations/LIMITATIONS.md) · Word: [`TA9-Limitations-Production.docx`](docs/limitations/TA9-Limitations-Production.docx).

**Main limitations**
- **One Job Service instance (SC-1).** SSE fan-out and the WS fan-in are per instance. Dispatch and updates are already multi-instance safe (leases, optimistic concurrency).
- **Processor state and outbox are in memory (SC-2, RE-1).** A restart loses queued work and unacked events. The reconciler recovers them within ~1–2 minutes.
- **No authentication, no TLS, no rate limiting (SE-1…SE-5).** Out of scope by assignment.
- **Single MongoDB node without auth or backups (ST-1).** Logs share it with the data (ST-3).
- **No metrics, traces or alerts (LG-1, LG-2).** Structured logs and health checks only.
- **Fixed concurrency limits and inline payloads (SC-3, SC-4).** Bounded and protected by input limits, but not adaptive.
- **`POST /api/jobs` has no idempotency key (API-1).** A client retry can create a duplicate job.
- **Docker Compose on one host, no CI/CD (OP-1, OP-2).**
- **Web UI (UI-1…UI-6):** Angular 17 is past official support, there are no browser end-to-end tests, and `ui/prebuilt` must be refreshed by hand.

**What I would do for production**
1. **P1, before production traffic:** API gateway with OIDC/JWT, TLS and rate limits; mTLS between services; MongoDB replica set with auth, TLS and backups; `Idempotency-Key`; CI/CD with image scanning; OpenTelemetry metrics, traces and alerts.
2. **P2, scale:** a durable broker (RabbitMQ/Kafka) for jobs and events; several Job Service instances with Redis pub/sub for SSE; Kubernetes with processors autoscaled on queue depth; object storage for document content; a dedicated log platform; retention policies.
3. **P3, refinements:** keyset paging, circuit breakers, a maximum job age, runtime log levels, API versioning, webhooks, load and chaos tests in CI.

Most of these steps replace a component rather than redesign the system. Storage is behind interfaces, handlers are idempotent, events are versioned, dispatch is lease-based and the correlation id maps onto W3C tracing.

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
Full disclosure (tools, workflow, what was generated, how it was verified, 17 representative prompts, 17 changed / rejected / corrected AI outputs, lessons learned): [`docs/ai-usage/AI-USAGE.md`](docs/ai-usage/AI-USAGE.md) · Word: [`TA9-AI-Usage.docx`](docs/ai-usage/TA9-AI-Usage.docx).

**Tool:** Claude Code (Anthropic, Claude Opus 5.5) in a terminal session. On my machine: .NET 8 SDK, Docker Desktop, PowerShell, Git, Postman.

**How it was used**
- The AI turned the assignment into multiple-choice questions with a recommendation for each. **I made every decision** (stack, storage, scope, error semantics, protocol) before any code was written; they are recorded in `docs/design/04-decisions.md` (D1–D27).
- It then wrote the design documents, and the code and tests **in 13 small steps**, explaining each step to me. Its environment had no .NET SDK, so **I built and ran every step on my machine** and sent back the failures. Nothing was committed before its tests were green.
- After the build I reviewed the system **against the assignment, section by section**. That review produced the storage fixes and the logging feature (console + MongoDB).

**AI-generated parts:** essentially all source code, tests, Docker and compose files, scripts, the Postman collection, the documents and the diagrams, all generated from my decisions. I reviewed it step by step and verified it with 440 automated test executions, Docker Desktop deployment, smoke tests and the 10 × 1,000 load.

**Representative prompts** (translated from Hebrew)
- "We'll write the solution in C# .NET 8, with Docker Compose, include the Audit service, and use MongoDB running as a Docker container. Some parts of the task are grey areas: ask me the questions and I'll decide before we write code. Write the code step by step so I can explain to the interviewer what I did, with unit tests for every step, and design documents."
- "11 tests failed: [pasted test output]"
- "Storage: we chose MongoDB. Code-wise, do we meet this section's requirement?"
- "The logs must be saved in MongoDB and printed to the console: Debug to debug crashes, Info to show the flow, Error when there are errors … and add tests that include logs."

**AI suggestions I changed or rejected (selection; 17 in the full document)**
1. **Python + FastAPI**, proposed because it was the AI's own toolchain. Rejected: C#/.NET 8 plus MongoDB, which the AI had not proposed.
2. **In-process Polly retries.** Replaced, with my approval, by retry state persisted in the job plus atomic leases (D24): retries survive restarts and never double-dispatch.
3. **Binary GUIDs in MongoDB.** 11 failures against a real MongoDB showed id queries returning nothing; GUIDs are now stored as strings (D22).
4. **Baking the corporate proxy's CA certificate into the image.** Rejected; added a host-publish, package-only Docker path instead.
5. **API catching MongoDB driver exceptions.** Changed after my storage review into a storage-neutral `StorageUnavailableException` (D25).
6. **Request logging on Serilog's static logger, and exception handling before the correlation middleware.** Both found by the logging tests I asked for (D-06, D-07) and fixed.
7. **Authentication / authorization.** I asked for it, and the AI implemented API keys with roles and service identities, with 32 tests. I then **rolled it back**: it is explicitly not required, and every caller (reviewer, Postman, scripts, UI) would need keys. It is documented as a limitation with a production plan (D29).

