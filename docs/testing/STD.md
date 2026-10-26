# Software Test Description (STD) — TA9 Document Processing System

| | |
|---|---|
| **Document** | Software Test Description (STD) |
| **System** | TA9 Document Processing — Job Service · Processor Service · Audit Service |
| **Test framework** | xUnit 2.9 · FluentAssertions 7 · NSubstitute · ASP.NET Core TestServer · Testcontainers (MongoDB 7) |
| **Automated tests** | 311 test methods → **440 executions** (theories and dual-store contract suites expanded) |
| **Related documents** | `docs/hld/TA9-HLD.docx` (design) · `docs/installation/TA9-Installation-Guide.docx` (environment) · `README.md` |

## 1. Introduction

### 1.1 Purpose
This document describes how the TA9 Document Processing System is verified. It lists the test levels and techniques, the test environment, every automated test case, the manual and system-level test procedures, the traceability from the assignment's requirements to the tests, and the latest execution results.

### 1.2 Scope
- **In scope:** functional behavior of the three services, the REST / WebSocket / SSE contracts, concurrency limits and the performance scenario (10 concurrent jobs × 1,000 documents), reliability (invalid input, unreachable services, restarts, lost or duplicate events, storage failures), storage replaceability, observability hooks, deployment (Docker Compose).
- **Out of scope:** security/penetration testing, long-duration soak tests, multi-instance deployments, UI testing (there is no UI).

### 1.3 Terms
| Term | Meaning |
|---|---|
| Fact / Theory | xUnit test with a single case / a parameterized test executed once per data row |
| Contract suite | One abstract test class executed against several implementations of an interface (InMemory and MongoDB) |
| TestServer | In-memory ASP.NET Core host: the full HTTP / WebSocket pipeline without opening ports |
| Testcontainers | Starts a real MongoDB in Docker for the duration of the tests |
| E2E | End-to-end test with all services running together |

## 2. Test strategy

### 2.1 Test levels
| Level | What it verifies | How | Count (methods / executions) |
|---|---|---|---|
| **Unit** | Domain rules and application logic in isolation | Plain objects, in-memory stores, fakes, manual clock | 218 / 323 |
| **Component** | A service's real pipeline: REST, WebSocket, SSE, middleware, DI, validation, error mapping | WebApplicationFactory / TestServer, real WebSocket clients, log capture; the Mongo log writer against a MongoDB container | 66 / 69 |
| **Contract** | That every storage implementation behaves identically | Same abstract suite against InMemory and real MongoDB (Testcontainers) | 21 / 42 |
| **Integration (E2E)** | The whole system working together, including failure scenarios and the performance requirement | Three services in-process, real WebSocket, MongoDB container | 6 / 6 |
| **Manual / system** | Deployed stack in Docker Desktop, API exploration, load and resilience | Smoke-test scripts, Postman collection, Swagger, curl, docker compose | §8 |

### 2.2 Techniques and test doubles
| Technique | Used for | Example |
|---|---|---|
| **Manual clock** (`ManualTimeProvider`) | Deterministic time: backoff, leases, staleness, throttling, without sleeping | `JobDispatcherTests`, `JobReconcilerTests`, `ProgressThrottleTests` |
| **Contract suites** | Proving storage is swappable; catching driver-specific bugs | `JobRepositoryContract` → InMemory + MongoDB |
| **Concurrency-tracking analyzer** | Measuring the maximum number of simultaneous analyses | `JobRunnerTests`, `JobWorkerServiceTests`, E2E 10 × 1,000 |
| **Gated analyzer** | Holding a job in *Processing* to test cancel and restart | `ProcessApiTests`, E2E restart / cancel |
| **Fake Job Service (TestServer WS)** | Real WebSocket traffic: ack, drop, replay | `JobServiceEventsClientTests` |
| **Fake processor / audit clients** | Scripting outcomes (accepted, busy, unreachable, rejected, 404) | `JobDispatcherTests`, `JobReconcilerTests` |
| **Failing repositories** | Storage outage behavior (503, no ack, exceptions propagate) | `JobsApiStorageFailureTests`, `ProcessorEventsEndpointTests` |
| **Exhaustive tables** | Every pair of job states checked against the designed lifecycle | `JobStateMachineTests` (36 cases) |
| **Log capture** (`ListLogger<T>`, `CapturingLogWriter`) | Asserting what is logged: level, message, job id, correlation id, exception. `CapturingLogWriter` replaces only MongoDB behind the real Serilog pipeline | `JobDispatcherLoggingTests`, `JobsApiLoggingTests`, `ServiceLoggingPipelineTests` |
| **Polling with timeout** (`Eventually`) | Waiting for asynchronous outcomes without fixed sleeps | API, WS and E2E tests |

### 2.3 Principles
- Every development step ended with all tests green before it was committed; the git history mirrors this.
- Tests assert behavior and contracts, not implementation details; names describe the expected behavior (they are used verbatim as test-case titles in §7).
- Deterministic by design: manual clock, bounded waits, isolated databases per test (`test_{guid}`), no shared mutable state between tests.
- Tests needing Docker are tagged `Category=Integration` so the rest can run anywhere.

## 3. Test environment
| Item | Value |
|---|---|
| OS | Windows 11 workstation (development and execution); Linux containers in Docker Desktop |
| Runtime / SDK | .NET 8 SDK 8.0.x |
| Containers | Docker Desktop 4.x; images `mongo:7.0`, `testcontainers/ryuk` (pulled automatically) |
| IDE / runner | `dotnet test` (VSTest adapter for xUnit 2.8) |
| Manual tools | PowerShell 5.1, curl, Postman, Swagger UI |

### 3.1 How to run

```powershell
dotnet test Ta9.sln                                     # all tests (Docker Desktop running)
dotnet test Ta9.sln --filter "Category!=Integration"    # no Docker needed
dotnet test tests/Ta9.IntegrationTests --logger "console;verbosity=detailed"   # E2E incl. performance output
dotnet test Ta9.sln --filter "FullyQualifiedName~JobDispatcherTests"          # one class
```

## 4. Test summary
| Test project | Classes | Test methods | Executions |
|---|---|---|---|
| Ta9.ServiceDefaults.Tests | 8 | 34 | 46 |
| Ta9.ProcessorService.Tests | 16 | 107 | 129 |
| Ta9.JobService.Tests | 22 | 152 | 241 |
| Ta9.AuditService.Tests | 4 | 12 | 18 |
| Ta9.IntegrationTests | 1 | 6 | 6 |
| **Total** | **51** | **311** | **440** |

### 4.1 Latest execution results
| Run | Result |
|---|---|
| Automated suite — `dotnet test Ta9.sln` (Windows 11, Docker Desktop, 2026-10-26) | **407 passed, 0 failed, 0 skipped** (≈ 26 s, 2026-10-27) — before the 33 logging tests (§7, Logging & Observability) were added; re-run pending |
| Docker Compose deployment — `docker compose ps` | All 4 containers **healthy** |
| Smoke test — 1 job × 3 documents | **Passed** — Completed, 3/3 succeeded, audit entry recorded |
| Load scenario — 10 jobs × 1,000 documents (`smoke-test.ps1 -Jobs 10 -Documents 1000`) | **Passed** — every job shown Completed with 1000/1000 succeeded (console output was truncated after the 7th job) |

## 5. Entry and exit criteria
- **Entry:** the solution builds; Docker Desktop is running for `Category=Integration`; ports 5001–5003 / 27017 free for manual tests.
- **Exit (per development step and before submission):** 100% of automated tests pass; manual procedures MT-01 to MT-03 pass on Docker Desktop; no open defect of severity High.
- **Suspension:** environment failures (Docker not running, network policy blocking image builds — see the Installation Guide §5) are fixed before re-running; they are not counted as product defects.

## 6. Requirements traceability
Requirements are taken from the home assignment (§3–§7 and the bonus §12).

| ID | Requirement | Verified by (test classes) | Manual |
|---|---|---|---|
| R1 | Job Service CRUD API `/api/jobs` | `JobsApiTests`, `JobManagerTests`, `CreateJobRequestValidatorTests` | MT-04 |
| R2 | Clear job lifecycle (Pending → Processing → Completed/Failed) | `JobStateMachineTests`, `JobTests`, `ProcessorEventHandlerTests` | — |
| R3 | Processor endpoint `/api/process` | `ProcessApiTests`, `ProcessingServiceTests`, `ProcessJobRequestValidatorTests` | — |
| R4 | Per-document word count, unique words, top-5, SHA-256, extra metadata | `DocumentAnalyzerTests`, `TokenizerTests`, `JobRunnerTests`, `EndToEndTests` | MT-02 |
| R5 | Processor reports job started / completed over WebSocket | `WebSocketJobEventPublisherTests`, `ProcessorEventSerializerTests`, `JobServiceEventsClientTests`, `ProcessorEventsEndpointTests`, `EndToEndTests` | MT-02 |
| R6 | ≥ 10 concurrent jobs, up to 1,000 documents each | `EndToEndTests`, `JobWorkerServiceTests` | MT-03 |
| R7 | No unbounded parallel work, no blocked threads, backpressure | `JobRunnerTests`, `JobWorkerServiceTests`, `ChannelJobQueueTests`, `ProcessApiTests`, `JobDispatcherTests` | — |
| R8 | Invalid input handled | `ProcessJobRequestValidatorTests`, `CreateJobRequestValidatorTests`, `JobsApiTests`, `ProcessApiTests`, `AuditApiTests`, `JobAuditRequestValidatorTests` | MT-05 |
| R9 | Processor cannot be reached | `JobDispatcherTests`, `HttpProcessorClientTests`, `EndToEndTests` | — |
| R10 | WebSocket disconnects and duplicate events | `EventOutboxTests`, `JobServiceEventsClientTests`, `ProcessorEventHandlerTests`, `ProcessorEventsEndpointTests` | MT-07 |
| R11 | Lost events / processor restart recovery | `JobReconcilerTests`, `JobRunnerTests`, `EndToEndTests` | MT-08 |
| R12 | Concurrent updates never lose writes | `JobRepositoryContract`, `JobRepositoryExtensionsTests` | — |
| R13 | Storage replaceable (persistent or in-memory) | `JobRepositoryContract`, `JobDocumentRepositoryContract`, `AuditRepositoryContract` | MT-01 |
| R14 | Storage failure handling | `JobsApiStorageFailureTests`, `MongoUnavailableTests`, `ProcessorEventsEndpointTests`, `ProcessorEventHandlerTests` | MT-09 |
| R15 | Cancellation | `ProcessingJobTests`, `ProcessingServiceTests`, `JobManagerTests`, `JobDispatcherTests`, `JobsApiTests`, `EndToEndTests` | MT-06 |
| R16 | Real-time status for clients (SSE) | `JobEventsApiTests`, `InMemoryJobUpdateBroadcasterTests`, `BroadcastingJobRepositoryTests` | MT-10 |
| R17 | Logging & observability: console + MongoDB, Debug/Information/Error levels, correlation id, health checks | `BatchingLogSinkTests`, `LogRecordTests`, `ServiceLoggingPipelineTests`, `MongoLogBatchWriterTests`, `JobDispatcherLoggingTests`, `JobRunnerLoggingTests`, `JobsApiLoggingTests`, `AuditApiLoggingTests`, `EndToEndTests`, `CorrelationTests`, `CorrelationIdMiddlewareTests`, `CorrelationIdHandlerTests` | MT-01, MT-11 |
| R18 | Bonus: Audit Service `POST /api/audit`, `GET /api/audit/{jobId}` | `AuditApiTests`, `AuditRepositoryContract`, `AuditPublisherTests`, `HttpAuditClientTests`, `EndToEndTests` | MT-02 |

## 7. Automated test case catalog
Section-specific subsets of this catalog, with the same IDs, appear in the topic documents: `docs/concurrency/TA9-Concurrency-Performance.docx` (§4 Concurrency & Performance, chapter 7) `docs/websocket/TA9-WebSocket-Behavior.docx` (WebSocket disconnect/reconnect, chapter 5) `docs/storage/TA9-Storage.docx` (§6 Storage, chapter 7) and `docs/logging/TA9-Logging-Observability.docx` (§7 Logging & Observability, chapter 5).

Each test case title is the test method name. ID prefixes: **SD** shared infrastructure, **PS** Processor Service, **JS** Job Service, **AS** Audit Service, **E2E** cross-service. *Theory ×N* runs N data rows; *×2 stores* runs once against InMemory and once against MongoDB.

### 7.1 Shared infrastructure (Ta9.ServiceDefaults.Tests)

#### CorrelationIdHandlerTests
**Level:** Unit · **Under test:** CorrelationIdHandler

Outgoing HttpClient calls carry the ambient correlation id.

| ID | Test case | Kind |
|---|---|---|
| SD-001 | Adds header from ambient scope | Fact |
| SD-002 | Does not add header without scope | Fact |
| SD-003 | Keeps explicit header | Fact |

#### CorrelationIdMiddlewareTests
**Level:** Component · **Under test:** CorrelationIdMiddleware

HTTP middleware on a TestServer: generates, reuses or replaces X-Correlation-Id and echoes it.

| ID | Test case | Kind |
|---|---|---|
| SD-004 | Generates id when header missing and echoes it | Fact |
| SD-005 | Reuses valid incoming id | Fact |
| SD-006 | Replaces invalid incoming id | Fact |

#### CorrelationTests
**Level:** Unit · **Under test:** Correlation

Correlation id validation (log-injection safe), scope nesting and flow across awaits.

| ID | Test case | Kind |
|---|---|---|
| SD-007 | IsValid accepts safe ids | Theory ×3 |
| SD-008 | IsValid rejects unsafe ids | Theory ×6 |
| SD-009 | IsValid rejects ids longer than 64 chars | Fact |
| SD-010 | BeginScope sets and restores current id | Fact |
| SD-011 | Current id flows across awaits | Fact |

#### ExponentialBackoffTests
**Level:** Unit · **Under test:** ExponentialBackoff

Exponential growth, cap and ±20% jitter (shared by WS reconnect and dispatch retries).

| ID | Test case | Kind |
|---|---|---|
| SD-012 | Grows exponentially up to the cap with jitter | Theory ×6 |
| SD-013 | Jitter spreads retries | Fact |

#### BatchingLogSinkTests
**Level:** Unit · **Under test:** BatchingLogSink

Bounded, non-blocking buffer; batches by size and time; flush on dispose; drops counted and reported; write failures isolated.

| ID | Test case | Kind |
|---|---|---|
| SD-014 | Writes events in order in batches no larger than the batch size | Fact |
| SD-015 | Flushes a partial batch after the flush interval | Fact |
| SD-016 | Dispose writes pending events before returning | Fact |
| SD-017 | Full buffer drops events without blocking and reports the count | Fact |
| SD-018 | Failed write is reported and later events still reach the store | Fact |
| SD-019 | Emit after dispose is counted as dropped and does not throw | Fact |

#### LogRecordTests
**Level:** Unit · **Under test:** LogRecord, MongoLogBatchWriter.ToBson

Mapping of a log event to the stored record: level, message, ids promoted, truncation, BSON shape.

| ID | Test case | Kind |
|---|---|---|
| SD-020 | Maps level rendered message service host and exception | Fact |
| SD-021 | Renders string values without quotes and other values as serilog does | Fact |
| SD-022 | Promotes correlation and job ids and keeps other properties as text | Fact |
| SD-023 | Truncates oversized message and exception | Fact |
| SD-024 | Bson document has indexed fields and safe property names | Fact |

#### ServiceLoggingPipelineTests
**Level:** Component · **Under test:** AddServiceDefaults logging (Serilog → filters → BatchingLogSink)

Real hosting setup with the database replaced by a capturing writer: levels, correlation id, unhandled errors, startup lines, disable switch, flush on stop.

| ID | Test case | Kind |
|---|---|---|
| SD-025 | Debug information and error are stored with service and correlation id | Fact |
| SD-026 | Mongo minimum level filters what is stored | Fact |
| SD-027 | Unhandled endpoint exception is stored as an error with the stack trace | Fact |
| SD-028 | Startup logs where logs go and that the service started | Fact |
| SD-029 | Disabled mongo logging stores nothing | Fact |
| SD-030 | Stopping the host flushes buffered records | Fact |

#### MongoLogBatchWriterTests
**Level:** Component · **Under test:** MongoLogBatchWriter · requires Docker (`Category=Integration`)

Real MongoDB: query by job id, TTL retention index and lookup indexes, retention change, fail fast when unreachable.

| ID | Test case | Kind |
|---|---|---|
| SD-031 | Stores records that can be queried by job id | Fact |
| SD-032 | Creates the retention ttl index and the lookup indexes | Fact |
| SD-033 | Changing the retention updates the existing ttl index | Fact |
| SD-034 | Unreachable database fails fast | Fact |

### 7.2 Processor Service (Ta9.ProcessorService.Tests)

#### DocumentAnalyzerTests
**Level:** Unit · **Under test:** DocumentAnalyzer

Word count, unique words, top-5 with alphabetical tie-break, SHA-256 vectors, lines, average and longest word, Hebrew, determinism.

| ID | Test case | Kind |
|---|---|---|
| PS-001 | Computes counts and top words | Fact |
| PS-002 | Returns at most five top words | Fact |
| PS-003 | Returns all words when fewer than five unique | Fact |
| PS-004 | Breaks frequency ties alphabetically | Fact |
| PS-005 | Handles empty document | Fact |
| PS-006 | Computes sha256 of utf8 text | Theory ×3 |
| PS-007 | Hashes original text not normalized words | Fact |
| PS-008 | Counts lines | Theory ×6 |
| PS-009 | Computes average and longest word | Fact |
| PS-010 | Breaks longest word ties alphabetically | Fact |
| PS-011 | Rounds average word length to two decimals | Fact |
| PS-012 | Counts characters of original text | Fact |
| PS-013 | Analyzes hebrew document | Fact |
| PS-014 | Is deterministic | Fact |
| PS-015 | Handles a large document correctly | Fact |
| PS-016 | Throws on null | Fact |

#### TokenizerTests
**Level:** Unit · **Under test:** Tokenizer

Unicode tokenization rules: punctuation, case, inner apostrophes, Hebrew niqqud and gershayim, NFC, emoji.

| ID | Test case | Kind |
|---|---|---|
| PS-017 | Splits on non word characters and lower cases | Theory ×6 |
| PS-018 | Keeps only inner apostrophes | Theory ×5 |
| PS-019 | Supports hebrew | Fact |
| PS-020 | Keeps hebrew niqqud inside the word | Fact |
| PS-021 | Keeps hebrew gershayim inside the word | Fact |
| PS-022 | Treats composed and decomposed accents as the same word | Fact |
| PS-023 | Returns no words for text without letters or digits | Theory ×3 |
| PS-024 | Ignores emoji and symbols | Fact |
| PS-025 | Throws on null | Fact |

#### JobRunnerTests
**Level:** Unit · **Under test:** JobRunner

Processing one job: results, per-document failure isolation, all-failed → Failed, cancellation, shutdown, document-parallelism bound, throttled progress.

| ID | Test case | Kind |
|---|---|---|
| PS-026 | Processes all documents and completes | Fact |
| PS-027 | Isolates a failing document | Fact |
| PS-028 | Fails the job when every document fails | Fact |
| PS-029 | Skips a job cancelled while queued | Fact |
| PS-030 | Stops when cancelled mid run without reporting completion | Fact |
| PS-031 | Abandons job silently on shutdown | Fact |
| PS-032 | Never exceeds document parallelism | Theory ×2 |
| PS-033 | Publishes throttled progress | Fact |

#### JobWorkerServiceTests
**Level:** Unit · **Under test:** JobWorkerService

Worker pool with real channel: total concurrency bound across jobs, crash isolation, clean shutdown.

| ID | Test case | Kind |
|---|---|---|
| PS-034 | Processes many jobs with bounded total concurrency | Fact |
| PS-035 | A crashing job does not kill the worker | Fact |
| PS-036 | Stops cleanly | Fact |

#### ProcessJobRequestValidatorTests
**Level:** Unit · **Under test:** ProcessJobRequestValidator

Processor input rules: job id, 1–1000 documents, indexes, null/oversized content (UTF-8 bytes), name length.

| ID | Test case | Kind |
|---|---|---|
| PS-037 | Accepts valid request | Fact |
| PS-038 | Rejects null request | Fact |
| PS-039 | Rejects empty job id | Fact |
| PS-040 | Rejects empty documents | Fact |
| PS-041 | Rejects too many documents | Fact |
| PS-042 | Accepts exactly the maximum number of documents | Fact |
| PS-043 | Rejects duplicate and negative indexes | Fact |
| PS-044 | Rejects null content | Fact |
| PS-045 | Measures content size in utf8 bytes | Fact |
| PS-046 | Accepts content at the size limit | Fact |
| PS-047 | Rejects too long name | Fact |
| PS-048 | Accepts empty content | Fact |

#### ProcessingJobTests
**Level:** Unit · **Under test:** ProcessingJob

Processor-side job state transitions under a lock; cancel signals the token; documents released when finished.

| ID | Test case | Kind |
|---|---|---|
| PS-049 | New job is queued with documents | Fact |
| PS-050 | Starts only once | Fact |
| PS-051 | Completes only from processing and releases documents | Fact |
| PS-052 | Cancel moves to cancelled and signals the token | Fact |
| PS-053 | Completion after cancel is ignored | Fact |
| PS-054 | Terminal jobs cannot be failed or cancelled | Fact |
| PS-055 | Response includes results only when asked | Fact |

#### ProcessingServiceTests
**Level:** Unit · **Under test:** ProcessingService

Submit idempotency, queue-full rejection (job forgotten for retry), cancel outcomes.

| ID | Test case | Kind |
|---|---|---|
| PS-056 | Accepts and stores a new job | Fact |
| PS-057 | Resubmitting the same job id is idempotent | Fact |
| PS-058 | Rejects when queue is full and forgets the job | Fact |
| PS-059 | Cancel unknown job returns not found | Fact |
| PS-060 | Cancel publishes once and is idempotent | Fact |
| PS-061 | Cancel finished job is rejected | Fact |

#### ProgressThrottleTests
**Level:** Unit · **Under test:** ProgressThrottle

Progress reported every 10% or 2 s, never at 100%, no duplicates for out-of-order counts.

| ID | Test case | Kind |
|---|---|---|
| PS-062 | Reports every ten percent but not at completion | Fact |
| PS-063 | Reports when the interval elapses between steps | Fact |
| PS-064 | Small jobs report each document except the last | Fact |
| PS-065 | Out of order counts do not produce duplicate reports | Fact |

#### ChannelJobQueueTests
**Level:** Unit · **Under test:** ChannelJobQueue

Bounded capacity (backpressure) and FIFO delivery.

| ID | Test case | Kind |
|---|---|---|
| PS-066 | Rejects when capacity is reached | Fact |
| PS-067 | Delivers jobs in fifo order and frees capacity | Fact |

#### InMemoryProcessingJobStoreTests
**Level:** Unit · **Under test:** InMemoryProcessingJobStore

Duplicate protection and TTL eviction of finished jobs only.

| ID | Test case | Kind |
|---|---|---|
| PS-068 | Rejects duplicate job id | Fact |
| PS-069 | Removes only finished jobs older than cutoff | Fact |

#### ProcessorEventSerializerTests
**Level:** Unit · **Under test:** ProcessorEventSerializer

WS envelope round trip, camelCase and string enums on the wire, ack parsing.

| ID | Test case | Kind |
|---|---|---|
| PS-070 | Envelope round trips with typed payload | Fact |
| PS-071 | Uses camel case and string enums on the wire | Fact |
| PS-072 | Ack round trips | Fact |
| PS-073 | Non ack messages are ignored | Theory ×3 |

#### WebSocketJobEventPublisherTests
**Level:** Unit · **Under test:** WebSocketJobEventPublisher

Lifecycle events queued with job metadata; progress dropped while disconnected; full outbox never throws.

| ID | Test case | Kind |
|---|---|---|
| PS-074 | Lifecycle events are queued with job metadata | Fact |
| PS-075 | Completed event carries summary and results | Fact |
| PS-076 | Progress is dropped while disconnected | Fact |
| PS-077 | Full outbox does not throw | Fact |

#### EventOutboxTests
**Level:** Unit · **Under test:** EventOutbox

At-least-once semantics: order, ack, ack-timeout resend, replay after reconnect, progress coalescing and dropping.

| ID | Test case | Kind |
|---|---|---|
| PS-078 | Rejects lifecycle events beyond capacity | Fact |
| PS-079 | Takes unsent events in order only once | Fact |
| PS-080 | Resends events whose ack timed out | Fact |
| PS-081 | Acknowledge removes the event | Fact |
| PS-082 | Reconnect makes unacknowledged events due again | Fact |
| PS-083 | Lifecycle events are buffered while disconnected | Fact |
| PS-084 | Progress is dropped while disconnected | Fact |
| PS-085 | Progress is coalesced to the latest per job and not kept for ack | Fact |
| PS-086 | Terminal event discards pending progress of the job | Fact |
| PS-087 | Disconnect discards pending progress | Fact |
| PS-088 | Wait for work completes when an event is added | Fact |

#### JobServiceEventsClientTests
**Level:** Component · **Under test:** JobServiceEventsClient

Real WebSocket against an in-memory fake Job Service: ordered delivery, buffering until reachable, replay after drop, ack-timeout resend.

| ID | Test case | Kind |
|---|---|---|
| PS-089 | Delivers events in order and clears outbox on ack | Fact |
| PS-090 | Buffers events until the job service becomes reachable | Fact |
| PS-091 | Replays unacknowledged events after reconnect | Fact |
| PS-092 | Resends when ack times out without reconnect | Fact |
| PS-093 | Sends progress while connected without waiting for ack | Fact |

#### ProcessApiTests
**Level:** Component · **Under test:** Processor REST API

/api/process through the full ASP.NET pipeline: 202/200/400/404/409/503 + Retry-After, cancel, empty content.

| ID | Test case | Kind |
|---|---|---|
| PS-094 | Post accepts job and results become available | Fact |
| PS-095 | Post same job twice returns 200 the second time | Fact |
| PS-096 | Post invalid request returns validation problem | Fact |
| PS-097 | Post accepts empty content | Fact |
| PS-098 | Post malformed json returns 400 | Fact |
| PS-099 | Post returns 503 with retry after when queue is full | Fact |
| PS-100 | Delete cancels a running job | Fact |
| PS-101 | Delete finished job returns 409 | Fact |
| PS-102 | Unknown job returns 404 | Theory ×2 |
| PS-103 | List returns known jobs | Fact |
| PS-104 | Liveness endpoint is healthy | Fact |

#### JobRunnerLoggingTests
**Level:** Unit · **Under test:** JobRunner (logging)

Flow at Information, progress at Debug, a failing document at Warning, a failed job at Error; job id on every line.

| ID | Test case | Kind |
|---|---|---|
| PS-105 | Successful job logs start and completion with the job id | Fact |
| PS-106 | Failing document is a warning with the exception and the job still completes | Fact |
| PS-107 | Job where every document fails is logged as an error | Fact |

### 7.3 Job Service (Ta9.JobService.Tests)

#### JobStateMachineTests
**Level:** Unit · **Under test:** JobStateMachine

Exhaustive 6×6 transition table compared with the designed lifecycle; terminal states.

| ID | Test case | Kind |
|---|---|---|
| JS-001 | Every transition matches the designed table | Theory ×36 |
| JS-002 | Terminal states are completed failed and cancelled | Theory ×6 |

#### JobTests
**Level:** Unit · **Under test:** Job aggregate

Aggregate rules: queued/dispatch retry, monotonic progress, completion without Started, late events after cancel, redispatch reset, audit flag.

| ID | Test case | Kind |
|---|---|---|
| JS-003 | New job is pending and due for dispatch | Fact |
| JS-004 | Marking queued clears dispatch scheduling | Fact |
| JS-005 | Dispatch retry counts only real failures | Theory ×2 |
| JS-006 | Dispatch retry is ignored once the job left pending | Fact |
| JS-007 | Progress on a queued job starts it | Fact |
| JS-008 | Progress never moves backwards or beyond total | Fact |
| JS-009 | Progress after a terminal state is ignored | Fact |
| JS-010 | Completion without a started event is accepted | Fact |
| JS-011 | Late completion after cancel is rejected | Fact |
| JS-012 | Failing records the reason | Fact |
| JS-013 | Returning to pending resets progress and counts the redispatch | Fact |
| JS-014 | Audit flag is set once and only for finished jobs | Fact |
| JS-015 | Clone is independent | Fact |

#### HttpProcessorClientTests
**Level:** Unit · **Under test:** HttpProcessorClient

HTTP status/exception → Accepted | Busy | Rejected | Unreachable; Retry-After; caller cancellation; best-effort cancel.

| ID | Test case | Kind |
|---|---|---|
| JS-016 | Submit maps status codes to outcomes | Theory ×6 |
| JS-017 | Submit 503 is busy with the retry after hint | Fact |
| JS-018 | Submit 503 without hint uses a default | Fact |
| JS-019 | Submit rejection includes the response body | Fact |
| JS-020 | Submit posts the contract payload | Fact |
| JS-021 | Submit connection failure is unreachable | Fact |
| JS-022 | Submit timeout is unreachable | Fact |
| JS-023 | Submit propagates caller cancellation | Fact |
| JS-024 | Get maps found not found and failures | Fact |
| JS-025 | Cancel treats already gone as success | Theory ×4 |
| JS-026 | Cancel never throws | Fact |

#### JobDocumentRepositoryContract
**Level:** Contract · **Under test:** IJobDocumentRepository · runs against **InMemory and MongoDB** · requires Docker (`Category=Integration`)

Same suite against InMemory and MongoDB: ordering, idempotent result writes, paging, delete isolation.

| ID | Test case | Kind |
|---|---|---|
| JS-027 | Returns documents of one job ordered by index | Fact · ×2 stores |
| JS-028 | Saving results is idempotent | Fact · ×2 stores |
| JS-029 | Results are paged by index | Fact · ×2 stores |
| JS-030 | Results for unknown documents are ignored | Fact · ×2 stores |
| JS-031 | Delete removes only the given job | Fact · ×2 stores |

#### JobRepositoryContract
**Level:** Contract · **Under test:** IJobRepository · runs against **InMemory and MongoDB** · requires Docker (`Category=Integration`)

Same suite against InMemory and real MongoDB: round trip, optimistic concurrency, 20 concurrent writers, paging, atomic dispatch claim with lease, stale and unpublished queries.

| ID | Test case | Kind |
|---|---|---|
| JS-032 | Insert then get round trips every field | Fact · ×2 stores |
| JS-033 | Get unknown job returns null | Fact · ×2 stores |
| JS-034 | Update persists changes and increments version | Fact · ×2 stores |
| JS-035 | Update with a stale version is rejected | Fact · ×2 stores |
| JS-036 | Changes to a loaded job are not visible until saved | Fact · ×2 stores |
| JS-037 | Concurrent updates never lose a write | Fact · ×2 stores |
| JS-038 | List filters orders newest first and pages | Fact · ×2 stores |
| JS-039 | Claim leases the due pending job until the lease expires | Fact · ×2 stores |
| JS-040 | Claim skips jobs that are not due or not pending | Fact · ×2 stores |
| JS-041 | Claim takes the longest waiting job first | Fact · ×2 stores |
| JS-042 | Concurrent claims never hand out the same job twice | Fact · ×2 stores |
| JS-043 | Stale query returns only silent active jobs oldest first | Fact · ×2 stores |
| JS-044 | Unpublished query returns finished jobs not yet audited | Fact · ×2 stores |
| JS-045 | Delete removes the job once | Fact · ×2 stores |

#### AuditPublisherTests
**Level:** Unit · **Under test:** AuditPublisher

Outbox-flag delivery to the Audit Service: publish once, payload, stop + retry when unavailable, 4xx not retried, job untouched.

| ID | Test case | Kind |
|---|---|---|
| JS-046 | Publishes finished jobs once and marks them | Fact |
| JS-047 | Summary carries the job outcome | Fact |
| JS-048 | Failed job summary includes the reason | Fact |
| JS-049 | Unavailable audit service keeps jobs pending and stops the batch | Fact |
| JS-050 | Rejected summary is not retried forever | Fact |
| JS-051 | Job state is untouched by publishing | Fact |

#### HttpAuditClientTests
**Level:** Unit · **Under test:** HttpAuditClient

Audit HTTP status → Recorded | Rejected | Unavailable.

| ID | Test case | Kind |
|---|---|---|
| JS-052 | Maps status codes | Theory ×5 |
| JS-053 | Posts to the audit endpoint | Fact |
| JS-054 | Connection failure is unavailable | Fact |

#### JobDispatcherTests
**Level:** Unit · **Under test:** JobDispatcher

Manual clock: accepted → Queued, 503 not counted, backoff then Failed, 4xx Failed, cancel during dispatch, lease after crash, bounded parallel batches, background loop.

| ID | Test case | Kind |
|---|---|---|
| JS-055 | Accepted dispatch queues the job with all documents in order | Fact |
| JS-056 | Busy processor delays the job without consuming an attempt | Fact |
| JS-057 | Unreachable processor backs off then fails the job | Fact |
| JS-058 | Rejected payload fails the job immediately | Fact |
| JS-059 | Job cancelled while dispatching is cancelled on the processor | Fact |
| JS-060 | A crashed dispatch is retried after the lease expires | Fact |
| JS-061 | Dispatches in bounded parallel batches | Fact |
| JS-062 | Background loop dispatches new jobs when signalled | Fact |
| JS-063 | Disabled dispatcher does nothing | Fact |

#### JobManagerTests
**Level:** Unit · **Under test:** JobManager

Create (documents then job, correlation id, wake dispatcher), results availability, cancel and delete outcomes per status, cancel propagation.

| ID | Test case | Kind |
|---|---|---|
| JS-064 | Create stores job and documents in order | Fact |
| JS-065 | Create wakes the dispatcher | Fact |
| JS-066 | Cancel propagates to the processor only when it has the job | Theory ×3 |
| JS-067 | Results are not available before completion | Fact |
| JS-068 | Cancel outcome depends on status | Theory ×6 |
| JS-069 | Delete outcome depends on status | Theory ×5 |

#### JobReconcilerTests
**Level:** Unit · **Under test:** JobReconciler

Lost events and processor restarts: stale selection, refresh, recover result, redispatch / fail after N, unreachable left alone, event-vs-recovery race.

| ID | Test case | Kind |
|---|---|---|
| JS-070 | Jobs with recent activity are not checked | Fact |
| JS-071 | Only queued and processing jobs are reconciled | Theory ×3 |
| JS-072 | Still running job gets fresh progress and liveness | Fact |
| JS-073 | Job waiting in the processor queue is only touched | Fact |
| JS-074 | Lost completion event is recovered with results | Fact |
| JS-075 | Lost failure event is recovered | Fact |
| JS-076 | Job lost by a restarted processor is redispatched | Fact |
| JS-077 | Job lost too many times fails | Fact |
| JS-078 | Unreachable processor leaves the job alone | Fact |
| JS-079 | Event arriving during reconciliation wins over redispatch | Fact |
| JS-080 | One failing job does not stop the pass | Fact |

#### JobRepositoryExtensionsTests
**Level:** Unit · **Under test:** UpdateAsync (optimistic retry)

Load → mutate → save with retries on conflict; not-found; declined mutation writes nothing; gives up after max attempts.

| ID | Test case | Kind |
|---|---|---|
| JS-081 | Applies the mutation and saves | Fact |
| JS-082 | Reports not found | Fact |
| JS-083 | Does not write when the mutation declines | Fact |
| JS-084 | Reloads and retries on conflict | Fact |
| JS-085 | Gives up after max attempts | Fact |

#### InMemoryJobUpdateBroadcasterTests
**Level:** Unit · **Under test:** InMemoryJobUpdateBroadcaster

SSE fan-out: per-job delivery, copies, disposal, release on terminal state, slow subscriber never blocks.

| ID | Test case | Kind |
|---|---|---|
| JS-086 | Subscribers receive snapshots of their job only | Fact |
| JS-087 | Published snapshot is a copy | Fact |
| JS-088 | Every subscriber gets the update | Fact |
| JS-089 | Disposed subscription stops receiving and completes | Fact |
| JS-090 | Terminal update is delivered then subscribers are released | Fact |
| JS-091 | Slow subscriber keeps the latest snapshots and never blocks the publisher | Fact |

#### BroadcastingJobRepositoryTests
**Level:** Unit · **Under test:** BroadcastingJobRepository

Decorator publishes successful writes only; dispatch claims are not published.

| ID | Test case | Kind |
|---|---|---|
| JS-092 | Successful update is published with the new version | Fact |
| JS-093 | Rejected update is not published | Fact |
| JS-094 | Dispatch claims are not published | Fact |

#### ProcessorEventHandlerTests
**Level:** Unit · **Under test:** ProcessorEventHandler

Applying events: transitions, Pending race, monotonic progress, results then Completed, replay/late/unknown/newer-schema ignored, storage failure propagates (no ack).

| ID | Test case | Kind |
|---|---|---|
| JS-095 | Started moves a queued job to processing | Fact |
| JS-096 | Events arriving before the dispatcher recorded the 202 still apply | Fact |
| JS-097 | Progress is recorded and never goes backwards | Fact |
| JS-098 | Completed stores results then completes the job | Fact |
| JS-099 | Replayed completion is ignored | Fact |
| JS-100 | Late completion of a cancelled job is ignored and stores no results | Fact |
| JS-101 | Out of order started after completed is ignored | Fact |
| JS-102 | Failed records code and reason | Fact |
| JS-103 | Unknown job is ignored | Fact |
| JS-104 | Unknown event type is ignored | Fact |
| JS-105 | Newer schema version is ignored | Fact |
| JS-106 | Malformed payload is ignored not retried | Fact |
| JS-107 | Storage failure propagates so the event is not acknowledged | Fact |

#### CreateJobRequestValidatorTests
**Level:** Unit · **Under test:** CreateJobRequestValidator

Public input rules: document count, names, job name length, null request.

| ID | Test case | Kind |
|---|---|---|
| JS-108 | Accepts valid request | Fact |
| JS-109 | Accepts the maximum number of documents | Fact |
| JS-110 | Rejects too many documents with a single error | Fact |
| JS-111 | Rejects too long job name | Fact |
| JS-112 | Job name is optional | Fact |
| JS-113 | Document name is required | Theory ×3 |
| JS-114 | Rejects null request | Fact |

#### JobEventsApiTests
**Level:** Component · **Under test:** SSE endpoint

Real streaming response read line by line: snapshot, every change, end at terminal state, monotonic ids, 404.

| ID | Test case | Kind |
|---|---|---|
| JS-115 | Streams snapshot then every change until the job finishes | Fact |
| JS-116 | Finished job returns one snapshot and closes | Fact |
| JS-117 | Event ids follow the job version | Fact |
| JS-118 | Unknown job is 404 | Fact |

#### JobsApiTests
**Level:** Component · **Under test:** Job REST API

/api/jobs end to end in-process: 201/400/404/409, correlation id, paging and filters, results, cancel (incl. propagation), delete, readiness.

| ID | Test case | Kind |
|---|---|---|
| JS-119 | Create returns 201 with location and pending job | Fact |
| JS-120 | Create accepts empty content | Fact |
| JS-121 | Create records the callers correlation id | Fact |
| JS-122 | Create without documents is rejected | Fact |
| JS-123 | Create with too many documents is rejected | Fact |
| JS-124 | Create with null content or missing name is rejected | Fact |
| JS-125 | Create with oversized document is rejected | Fact |
| JS-126 | Create with malformed json returns 400 | Fact |
| JS-127 | Get returns the job or 404 | Fact |
| JS-128 | List pages newest first and filters by status | Fact |
| JS-129 | List rejects invalid query | Theory ×3 |
| JS-130 | Results are 409 until the job completes then paged | Fact |
| JS-131 | Results of unknown job is 404 | Fact |
| JS-132 | Cancel is idempotent and conflicts once finished | Fact |
| JS-133 | Cancelling a dispatched job tells the processor | Fact |
| JS-134 | Delete requires a finished job and removes its documents | Fact |
| JS-135 | Readiness reports storage health | Fact |

#### JobsApiStorageFailureTests
**Level:** Component · **Under test:** ApiExceptionHandler

A storage-neutral `StorageUnavailableException` surfaces as a retryable 503 ProblemDetails (the API does not reference MongoDB types).

| ID | Test case | Kind |
|---|---|---|
| JS-136 | Storage timeout returns 503 problem | Fact |
| JS-152 | Storage outage is logged as an error with the correlation id | Fact |

#### ProcessorEventsEndpointTests
**Level:** Component · **Under test:** WebSocket endpoint

Real WebSocket: ack after handling, duplicates/unknown acked, bad message skipped, per-connection order, no ack on storage failure.

| ID | Test case | Kind |
|---|---|---|
| JS-137 | Handled events are acknowledged and applied | Fact |
| JS-138 | Duplicates and unknown jobs are acknowledged too | Fact |
| JS-139 | Unreadable message is skipped and the connection keeps working | Fact |
| JS-140 | Events are processed in order per connection | Fact |
| JS-141 | Plain http request is rejected | Fact |
| JS-142 | Event that cannot be stored is not acknowledged | Fact |

#### MongoUnavailableTests
**Level:** Unit · **Under test:** MongoJobRepository, MongoJobDocumentRepository (MongoGuard)

An unreachable MongoDB (closed port, 300 ms server-selection timeout, no Docker) is translated to `StorageUnavailableException`.

| ID | Test case | Kind |
|---|---|---|
| JS-143 | Job repository reads and writes report storage unavailable | Fact |
| JS-144 | Failed update keeps the version token unchanged | Fact |
| JS-145 | Document repository reports storage unavailable | Fact |

#### JobDispatcherLoggingTests
**Level:** Unit · **Under test:** JobDispatcher (logging)

Dispatch flow at Information, details at Debug, unreachable → Warning then Error, rejected and unexpected failures at Error.

| ID | Test case | Kind |
|---|---|---|
| JS-146 | Accepted dispatch logs the flow at information and details at debug | Fact |
| JS-147 | Unreachable processor logs warnings then an error when giving up | Fact |
| JS-148 | Rejected payload is logged as an error | Fact |
| JS-149 | Unexpected failure is logged as an error with the exception | Fact |

#### JobsApiLoggingTests
**Level:** Component · **Under test:** Job Service logging pipeline

What an API call stores: Information + Debug with job id and correlation id; client errors are not logged as errors.

| ID | Test case | Kind |
|---|---|---|
| JS-150 | Creating a job stores info and debug records with job id and correlation id | Fact |
| JS-151 | Invalid request is logged without error level | Fact |

### 7.4 Audit Service (Ta9.AuditService.Tests)

#### AuditApiTests
**Level:** Component · **Under test:** Audit REST API

Record and get, first write wins, 404, validation problem, readiness.

| ID | Test case | Kind |
|---|---|---|
| AS-001 | Records a summary and returns it | Fact |
| AS-002 | Recording twice is idempotent and keeps the first entry | Fact |
| AS-003 | Unknown job is 404 | Fact |
| AS-004 | Invalid summary is rejected | Fact |
| AS-005 | Readiness reports storage | Fact |

#### JobAuditRequestValidatorTests
**Level:** Unit · **Under test:** JobAuditRequestValidator

Terminal statuses only, consistent counts, completion after creation.

| ID | Test case | Kind |
|---|---|---|
| AS-006 | Accepts terminal statuses | Theory ×3 |
| AS-007 | Rejects non terminal status | Theory ×3 |
| AS-008 | Rejects inconsistent counts | Fact |
| AS-009 | Rejects completion before creation | Fact |

#### AuditRepositoryContract
**Level:** Contract · **Under test:** IAuditRepository · runs against **InMemory and MongoDB** · requires Docker (`Category=Integration`)

Same suite against InMemory and MongoDB: first write wins, unknown job.

| ID | Test case | Kind |
|---|---|---|
| AS-010 | First write wins | Fact · ×2 stores |
| AS-011 | Unknown job returns null | Fact · ×2 stores |

#### AuditApiLoggingTests
**Level:** Component · **Under test:** Audit Service logging pipeline

Recording logs Information with the job id; a duplicate logs Debug.

| ID | Test case | Kind |
|---|---|---|
| AS-012 | Recording logs information and a duplicate logs debug | Fact |

### 7.5 Cross-service integration (Ta9.IntegrationTests)

#### EndToEndTests
**Level:** Integration (E2E) · **Under test:** Whole system · requires Docker (`Category=Integration`)

Job, Processor and Audit services in-process over REST + real WebSocket + MongoDB: full flow, 10 × 1,000 documents, unreachable processor, processor restart, cancellation, logs of all three services in MongoDB.

| ID | Test case | Kind |
|---|---|---|
| E2E-001 | Job flows through processor websocket and audit | Fact |
| E2E-002 | Ten concurrent jobs of 1000 documents complete with bounded parallelism | Fact |
| E2E-003 | Unreachable processor fails the job after retries | Fact |
| E2E-004 | Job lost by a processor restart is redispatched and completes | Fact |
| E2E-005 | Cancelling a running job stops it on the processor | Fact |
| E2E-006 | Job flow is logged to mongodb by all three services under one correlation id | Fact |

## 8. Manual and system test procedures
Executed against the stack deployed with Docker Desktop (Installation Guide §4).

| ID | Title | Preconditions | Steps | Expected result | Status |
|---|---|---|---|---|---|
| MT-01 | Deployment and health | Docker Desktop running; repository cloned | 1) `docker compose up --build -d` (or `scripts/docker-up-prebuilt.ps1` on restricted networks) 2) `docker compose ps` 3) Open http://localhost:5001/health/ready, :5002, :5003 | All four containers *(healthy)*; each ready endpoint returns `Healthy` | Passed (2026-10-26) |
| MT-02 | Smoke test — full flow | MT-01 passed | 1) `powershell -ExecutionPolicy Bypass -File .\scripts\smoke-test.ps1` | Job Completed; 3/3 documents succeeded; word counts, top words and SHA-256 printed; audit entry `Completed` | Passed (2026-10-26) |
| MT-03 | Performance — 10 jobs × 1,000 documents | MT-01 passed | 1) `powershell -ExecutionPolicy Bypass -File .\scripts\smoke-test.ps1 -Jobs 10 -Documents 1000` | All 10 jobs Completed with 1000 succeeded / 0 failed; total time printed | Passed (2026-10-26) |
| MT-04 | API walkthrough (Postman) | MT-01 passed | 1) Import `docs/postman/Ta9.postman_collection.json` 2) Run folders 1–4 in order (Collection Runner) | All embedded Postman tests pass (201, 200/409, 202/409, 400, 404 problem+json, 204) | Not yet executed |
| MT-05 | Invalid input | MT-01 passed | 1) Swagger: `POST /api/jobs` with `documents: []` 2) with a document whose `content` is null 3) with 1,001 documents | 400 ValidationProblemDetails listing the offending fields; nothing is created | Not yet executed |
| MT-06 | Cancel a running job | MT-01 passed | 1) Create a large job (Postman folder 2) 2) `POST /api/jobs/{id}/cancel` immediately 3) `GET /api/jobs/{id}` and `GET :5002/api/process/{id}` | 202; job `Cancelled` in both services; audit entry `Cancelled`; no later status change | Not yet executed |
| MT-07 | WebSocket reconnect and replay | MT-01 passed | 1) Start a job 2) `docker compose restart job-service` 3) `docker compose logs -f processor-service` | Processor logs `connection failed (attempt N)` then `Connected … N events pending`; job ends Completed; no event lost | Not yet executed |
| MT-08 | Processor restart mid-job | MT-01 passed | 1) Start the 10 × 1,000 load 2) `docker compose restart processor-service` while jobs are Processing 3) Watch `GET /api/jobs?status=Processing` | Within ~60–90 s the reconciler redispatches lost jobs; all jobs end Completed | Not yet executed |
| MT-09 | Storage outage | MT-01 passed | 1) `docker compose stop mongo` 2) `GET /api/jobs` and `/health/ready` on :5001 3) `docker compose start mongo` | 503 *Storage unavailable* ProblemDetails within ~5 s; readiness Unhealthy; service recovers after Mongo starts | Not yet executed |
| MT-10 | Live status stream (SSE) | MT-01 passed | 1) Create a job 2) `curl.exe -N http://localhost:5001/api/jobs/<id>/events` | `event: status` lines: snapshot, then Processing with progress, then Completed; stream closes | Not yet executed |
| MT-11 | Logs in console and MongoDB | MT-02 passed | 1) `docker compose logs --tail 50 job-service processor-service` 2) `docker exec -it ta9-mongo-1 mongosh ta9_logs --eval "db.logs.find({jobId:'<id>'}).sort({timestamp:1})"` with the job id from MT-02 3) `db.logs.find({level:'Error'})` | Console shows the flow (Information); MongoDB holds Debug + Information records of all three services for the job with one `correlationId`; TTL index `ttl_timestamp` exists | Not yet executed |

## 9. Defects found by testing
Defects detected by the automated tests during development; all fixed before submission.

| # | Detected by | Symptom | Root cause | Fix / status |
|---|---|---|---|---|
| D-01 | `MongoJobRepositoryTests`, `MongoJobDocumentRepositoryTests` (11 failures) | Queries by id / jobId returned nothing; unfiltered reads worked | MongoDB.Driver 2.x legacy GUID mode encoded stored and query values differently | GUIDs stored as strings, explicit per-member serializers (decision D22) — **Fixed** |
| D-02 | `ProcessApiTests.Unknown_job_returns_404` | 404 returned `application/json` instead of `application/problem+json` | `[Produces("application/json")]` overrode ProblemDetails | Attribute removed — **Fixed** |
| D-03 | Code review while writing `JobsApiTests` (regression test `Post_accepts_empty_content`) | Empty document content rejected with 400; whole jobs would fail in the processor | MVC implicit `[Required]` on non-nullable strings | Implicit rule disabled in all services (D23) — **Fixed** |
| D-04 | `JobRunnerTests` (3 failures) | Unexpected `JobProgress` events in lifecycle assertions | Test expectation: small jobs legitimately report per-document progress | Lifecycle assertions ignore progress; progress verified separately — **Fixed (test)** |
| D-05 | Manual MT-02 | Smoke test printed `Created  job(s)` without the count | Windows PowerShell 5.1 has no `.Count` on a single object | Result wrapped in `@()` — **Fixed** |

## 10. Risks and limitations of the test approach
- Performance is verified functionally (completion, exactly-once analysis, concurrency bound) and timed manually; there is no automated latency/throughput threshold or long-running soak test.
- Multi-instance behavior (several Job Service instances) is covered only at the storage level (atomic claims, optimistic concurrency); SSE fan-out across instances is not tested because it is not supported.
- E2E tests run the services in one process on TestServer; network faults between containers are exercised manually (MT-07 to MT-09).
- Security testing is out of scope (no authentication by assignment).
