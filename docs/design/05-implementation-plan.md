# 05 — Implementation Plan

Work proceeds in small steps. Each step ends with **green tests and a commit**, so the git history tells the story step by step. Unit tests come with every step. Integration tests are added where a real boundary (Mongo, HTTP, WS) appears.

| Step | Scope | Tests | Est. |
|---|---|---|---|
| **0** | Repo skeleton: `.sln`, projects, `Directory.Build.props` (nullable, C# 12), `Directory.Packages.props` (central package versions), `.gitignore`, `.editorconfig`, `Ta9.ServiceDefaults` (Serilog, correlation id, ProblemDetails, health) | `Ta9.ServiceDefaults.Tests`: correlation id validation, scope/AsyncLocal flow, middleware (generate / reuse / replace invalid), outgoing HttpClient propagation | 0.5h |
| **1** | Processor domain: `Tokenizer`, `DocumentAnalyzer` (word count, unique, top 5, SHA-256, metadata) | Tokenization theory cases (punctuation, Hebrew, apostrophes, case, empty), top-5 tie-break, known SHA-256 vectors, determinism | 0.75h |
| **2** | `Ta9.Contracts` (REST DTOs, shared limits). Processor engine + API: in-memory store (TTL eviction), bounded channel, worker pool, per-job doc parallelism, cancellation, progress throttling, `/api/process` CRUD, validation, Swagger. Events go to a logging placeholder publisher | Queue full → 503, idempotent POST, cancel, **concurrency bound respected** (instrumented analyzer counts max in-flight), per-doc failure isolation, API tests via WebApplicationFactory | 1.25h |
| **3** | WS event contracts + Processor WS client: event envelope, serializer, outbox with ack, reconnect with backoff | Outbox: ack removes, unacked replayed on reconnect, bounded overflow, progress never queued. Throttle math. Serializer round-trip | 1h |
| **4** | Job Service domain + storage: `Job`, `JobStateMachine`, `IJobRepository` (InMemory + Mongo), indexes, conditional transitions, bulk results | **Exhaustive transition table** (theory over all from→to pairs). Mongo repo via Testcontainers: conditional update rejects stale status, results upsert idempotent | 1h |
| **5** | Job Service API: create/list/get/results/cancel/delete, validation, paging | API tests for every endpoint and every 4xx | 0.75h |
| **6** | Dispatcher: wake signal + tick, atomic claim with lease, bounded parallel batches, persisted retry schedule (exponential backoff + jitter, shared with the WS client), 503 handling, attempt counting, cancel propagation to the Processor | Fake processor client: success → Queued, 503 → stays Pending with no attempt consumed, unreachable ×5 → Failed, 400 → Failed | 0.75h |
| **7** | WS endpoint + `ProcessorEventHandler` (guard, ack after handling, progress `max`) | Duplicate event = no-op + ack, out-of-order, late event after Cancelled, unknown job, Mongo failure ⇒ no ack | 1h |
| **8** | Reconciler | Stale + 404 → Pending (redispatch++), limit → Failed, remote Completed → applied, fresh jobs untouched | 0.5h |
| **9** | SSE endpoint + in-process broadcaster | Snapshot first, receives updates, closes on terminal, slow subscriber doesn't block (bounded per-subscriber channel) | 0.5h |
| **10** | Audit Service + Job Service `AuditPublisher` (outbox flag) | Audit API (idempotent upsert, 404). Publisher retries and marks published | 0.75h |
| **11** | Cross-service E2E: Testcontainers Mongo + in-process Job and Processor hosts connected over real WS | Happy path, **10 concurrent jobs × 1,000 docs**, processor down → Failed, processor restart → redispatch | 0.75h |
| **12** | Docker: one parameterized multi-stage Dockerfile (non-root, cached restore layer), `docker-compose.yml` (mongo + 3 services, healthchecks, startup ordering), Postman collection, smoke-test scripts (PowerShell + bash) | `docker compose up --build`, then `scripts/smoke-test.ps1` or the Postman collection | 0.5h |
| **13** | README: run, test, architecture summary, decisions, concurrency, WS behaviour, limitations, **AI usage** | — | 0.5h |

**Total: about 10.5h.** If time runs short, steps 9 (SSE) and 10 (Audit) are the first to trim. Steps 0–8 + 11–13 cover all mandatory requirements.

## Environment prerequisites (this workstation)
- The .NET 8 SDK is not installed. It goes into `~/.dotnet` with `dotnet-install.sh`, so no root is needed.
- nuget.org is not reachable from this network. Packages come through an internal package mirror, configured in the **user-level** `~/.nuget/NuGet/NuGet.Config` only. The repo itself contains no feed configuration or credentials, and it builds against nuget.org anywhere else.
- Docker here is Podman-compatible. Testcontainers needs `DOCKER_HOST` pointed at the Podman socket.
