# 04 — Decision Log

Each entry records the choice, the alternatives considered, and why. These are the "grey areas" the assignment left open.

| # | Topic | Decision | Alternatives rejected | Rationale |
|---|---|---|---|---|
| D1 | Stack | C# / .NET 8, ASP.NET Core **Controllers** | Minimal APIs | Familiar, first-class model validation, ProblemDetails and Swagger annotations |
| D2 | Storage | **MongoDB** (docker container) behind repository interfaces, plus an InMemory implementation for tests | SQLite, in-memory only | Natural fit for document-shaped jobs and results. Persistence survives restarts. The interface keeps it replaceable |
| D3 | Document input | Inline JSON `{name, content}[]` | Multipart upload, URLs | Simple, Swagger-friendly, matches "simple business logic" |
| D4 | Dispatch | **Background dispatcher** with retry/backoff. POST returns 201 immediately | Synchronous call inside POST | Client is decoupled from processor availability. Jobs survive restarts. Retry is possible |
| D5 | Result delivery | Results travel inside `JobCompleted`. `GET /api/process/{id}` is the fallback | Pull after event, per-doc events | One hop on the happy path. About 400 KB for 1,000 documents is fine for a WS message |
| D6 | Partial failure | Job `Completed` with per-document status. `Failed` only on a system error or when all documents failed | All-or-nothing, threshold | Don't throw away 999 good results because of one bad document |
| D7 | WS reliability | Reconnect + bounded outbox **with ack** + reconciler | Reconnect only, outbox only | Defence in depth. The ack shows which events actually arrived (a TCP send succeeding proves nothing) |
| D8 | Duplicates / ordering / races | State-machine guard in the `Job` aggregate + **optimistic concurrency** (`Version` token, load → mutate → conditional replace → retry on conflict). Progress uses `max()` | eventId dedupe table, sequence numbers, status-filtered updates in the repository | Idempotency is built into the state itself, with no extra storage. Every rule lives in the domain and is unit-testable without a DB; the repository only compares versions |
| D9 | Backpressure | Bounded channel (100) + 10 workers. 503 + Retry-After when full | Unbounded queue | Required: "avoid unbounded parallel work" |
| D10 | Processor restart | Reconciler redispatches up to 3 times, then Failed | Fail immediately, persist processor state | Processor stays stateless. Job Service is the single source of truth. Analysis is idempotent |
| D11 | CRUD semantics | `POST /cancel` → Cancelled. `DELETE` only for terminal jobs (409 otherwise). No PUT | DELETE = cancel, no cancel | Clear semantics. Jobs are immutable |
| D12 | Mongo layout | `jobs` (small) + `job_documents` (one row per document). Limits: 1,000 docs, 100 KB per doc, 10 MB per body. Paged results | Single embedded document | Avoids the 16 MB BSON limit. Status polling stays cheap |
| D13 | Client real-time | **SSE** `GET /api/jobs/{id}/events` | Polling only | One-way server→client updates are exactly what SSE is for, and it can be tested with curl |
| D14 | Tokenization | Unicode letters/digits (incl. Hebrew), with combining marks (niqqud) and inner apostrophes/geresh kept. Text is NFC-normalized and case-insensitive (`ToLowerInvariant`). Hyphens split words, numbers count as words, emoji and symbols are ignored. No stopwords. Ties broken alphabetically (ordinal). SHA-256 over the **original** UTF-8 text | Stopword filtering, regex `\w+` | Deterministic, language-neutral, easy to test. A hand-written scanner is explicit about each rule and allocates only the tokens |
| D15 | Solution structure | One project per service (folders Api/Application/Domain/Infrastructure) + `Ta9.Contracts` | Full Clean Architecture (15 projects) | Clear layering without over-engineering an 8-hour task |
| D16 | Tests | xUnit + NSubstitute + FluentAssertions **7.x** + WebApplicationFactory + Testcontainers.MongoDb | Unit-only, Moq | Real Mongo and real WS in integration tests. FluentAssertions 8 changed to a commercial license |
| D17 | Observability | Serilog JSON + correlation id + health checks | OpenTelemetry, plain ILogger | Covers the assignment at low cost. OpenTelemetry is listed under "production improvements" |
| D18 | Extra lifecycle states | Added `Queued` and `Cancelled` to the suggested lifecycle | Exact lifecycle from the PDF | `Queued` separates "dispatched" from "not yet dispatched", which the dispatcher and reconciler both need. `Cancelled` is a user outcome, not a failure |
| D19 | Shared hosting code | Small `Ta9.ServiceDefaults` library (Serilog, correlation middleware, ProblemDetails, health) | Copy into each service | Avoids roughly 3 × 80 duplicated lines. It follows the Aspire "ServiceDefaults" convention |
| D20 | Shared DTOs in persistence | `job_documents.result` stores the `DocumentResultDto` contract type, and the job stores `ProcessingSummaryDto` | Separate domain + persistence models with mapping | They are immutable value records that both services already agree on. A mapping layer would duplicate about 15 fields for no behavioral gain. Revisit if the Job Service ever needs a different shape |
| D21 | No multi-document transactions | Insert the documents first, then the job (the job record acts as the commit marker). Delete the job first, then its documents | Mongo transactions (they need a replica set) | A crash in between leaves only orphan documents that nothing references, which is harmless and easy to sweep |
| D22 | Mongo GUID encoding | GUIDs stored as **strings**, with the serializer set explicitly per member and registered globally | Binary UUID (subtype 4) | MongoDB.Driver 2.x still defaults to the legacy GUID mode, where binary GUIDs written and queried through different paths can silently fail equality filters. Our Mongo integration tests caught exactly that. Strings remove the ambiguity and are readable in the shell, at the cost of slightly larger documents |

## Defaults (configurable through appsettings or environment variables)

| Setting | Default |
|---|---|
| Processor `MaxConcurrentJobs` / `QueueCapacity` / `DocParallelismPerJob` | 10 / 100 / 2 |
| Processor `ResultsTtl` | 1h |
| Processor outbox capacity / WS reconnect backoff | 10,000 / 1s → 30s |
| Dispatcher `MaxDispatchAttempts` / backoff | 5 / 2s → 60s + jitter |
| Reconciler interval / stale-after / max redispatch | 30s / 60s / 3 |
| Limits: docs per job / doc size / body size | 1,000 / 100 KB / 10 MB |
