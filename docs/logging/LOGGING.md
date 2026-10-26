# Logging & Observability — TA9 Document Processing System

| | |
|---|---|
| **Assignment section** | §7 Logging & Observability |
| **Requirement** | Meaningful logging for job lifecycle events, failures and retries. Health checks and correlation ids are a plus |
| **Our requirements** | Logs stored in **MongoDB** and printed to the **console**. **Debug** logs to investigate failures, **Information** logs that show the flow, **Error** logs when something fails |
| **Chosen strategy** | **One Serilog pipeline in `Ta9.ServiceDefaults`**, shared by the three services. Two sinks: console (Information and up, synchronous) and our own **batched MongoDB sink** (Debug and up, bounded and non-blocking, 7-day TTL). Every record carries `Service`, `CorrelationId` and `JobId` |
| **Diagram** | draw.io page **11. Logging** in `docs/hld/diagrams/ta9-hld.drawio` |
| **Troubleshooting** | Installation Guide §5 *Troubleshooting — where to look* (does not start · stuck · crashes) |

## 1. What we built

![Logging & observability](../hld/images/logging.png)

### 1.1 One pipeline, two destinations

| Destination | Levels (default) | Purpose | How to read it |
|---|---|---|---|
| **Console** | Information and up | The live **flow** and every warning and error. Written synchronously, so the last lines before a crash are always there | `docker compose logs -f <service>` · Docker Desktop |
| **MongoDB `ta9_logs.logs`** | **Debug** and up | Full history of all three services in one searchable place, for **investigating failures after the fact** | `mongosh ta9_logs` → `db.logs.find({ jobId: … })` |

Both destinations are set up once in `AddServiceDefaults` (`Ta9.ServiceDefaults`), so the three services log the same way. The levels and the database are configuration only:

| Setting | Default | Meaning |
|---|---|---|
| `Serilog:MinimumLevel:Default` | `Debug` | Global gate (`Microsoft.*` = Information, `Microsoft.AspNetCore` = Warning to keep framework noise out) |
| `Logging:Console:MinimumLevel` | `Information` | Console filter |
| `Logging:Mongo:Enabled` | `true` | Store logs in MongoDB |
| `Logging:Mongo:ConnectionString` / `Database` / `Collection` | `mongodb://localhost:27017` / `ta9_logs` / `logs` | Where (Docker: `mongodb://mongo:27017`) |
| `Logging:Mongo:MinimumLevel` | `Debug` | MongoDB filter |
| `Logging:Mongo:RetentionDays` | `7` | TTL index: MongoDB deletes older records |
| `Logging:Mongo:BatchSize` / `FlushInterval` / `QueueCapacity` | `100` / `1 s` / `10,000` | Batching and the bounded buffer |

### 1.2 The MongoDB sink (`BatchingLogSink` + `MongoLogBatchWriter`)

Writing a log line must never slow down a request or a worker, and it must never take a service down. The sink is built around that:

| Property | How |
|---|---|
| **Never blocks the caller** | `Emit` only puts the event in a bounded `Channel` (10,000). When it is full, the event is dropped and counted. No waiting and no I/O on the caller's thread |
| **Efficient** | One background loop writes `InsertMany` batches of up to 100, or every 1 s, whichever comes first |
| **Survives a database outage** | A failed write is caught: the batch is counted as lost and a line is printed to stderr (`[ta9-log-sink] …`, at most once per 30 s). The service keeps running and the console still has everything |
| **Losses are visible** | The next successfully stored batch starts with a Warning *"N log events were not stored (buffer full or log database unavailable)"* |
| **Fails fast** | The writer's Mongo client uses a 3 s server-selection timeout (the driver default is 30 s) |
| **Flushed on stop and on crash** | Disposing the logger at shutdown writes what is left (max 5 s). On an unhandled exception the sink is flushed before the process exits |
| **Bounded storage** | TTL index `ttl_timestamp` (7 days). Changing `RetentionDays` updates the index in place (`collMod`) |
| **Searchable** | Indexes on `{jobId, timestamp}`, `{correlationId, timestamp}`, `{level, timestamp}`, `{service, timestamp}` |
| **Safe documents** | Plain BSON (no class maps, so it cannot clash with a service's own Mongo mapping). Message ≤ 8 KB, exception ≤ 16 KB, property ≤ 1 KB, `.`/`$` removed from field names |

A stored record:

```json
{
  "timestamp": ISODate("2026-10-27T09:14:03.512Z"),
  "level": "Information",
  "service": "processor-service",
  "host": "3f2a9c1b7d40",
  "message": "Job completed: 1000 succeeded, 0 failed in 2140 ms",
  "messageTemplate": "Job completed: {Succeeded} succeeded, {Failed} failed in {DurationMs} ms",
  "sourceContext": "Ta9.ProcessorService.Application.JobRunner",
  "correlationId": "5be0c7a2f1e94d0c9a1f0d3b6c2e8a11",
  "jobId": "8d6f3c2e-41a7-4c55-9a3e-0b5d7e9f1a22",
  "properties": { "Succeeded": "1000", "Failed": "0", "DurationMs": "2140" }
}
```

### 1.3 Levels — what is logged where

| Level | Rule | Examples (actual messages) |
|---|---|---|
| **Debug** | Detail that explains *why*: decisions, intermediate results, each hop | `Claimed job … for dispatch (previous attempts 0, lease until …)` · `POST api/process for job … returned 202` · `Processor answered Accepted for job … (1000 documents)` · `Worker 3 picked up job …` · `Progress 300/1000` · `Queued JobCompleted for job … (2 events pending)` · `Received JobCompleted … (schema 1)` · `Acknowledged JobCompleted …` · `Processor lookup for job … (Processing): Found Processing` · `Audit entry for job … already exists; keeping the first one` |
| **Information** | The **flow** of a job and of the services | `Job … created with 3 documents` · `Job … dispatched to the processor` · `Job … accepted with 3 documents` · `Job started with 3 documents` · `Job completed: 3 succeeded, 0 failed in 12 ms` · `Applied JobCompleted to job …` · `Recorded audit entry for job … (Completed)` · `HTTP POST /api/jobs responded 201 in 8.1 ms` · `Service started: environment Production, …, listening on http://[::]:8080` |
| **Warning** | A problem the system handles by itself | `Processor unreachable (attempt 2): … Next try at …` · `Document 7 (a.txt) failed` · `Job Service events connection failed (attempt 3)` · `Processor busy; job … will be dispatched again at …` · `Processor no longer knows job …; redispatching` |
| **Error** | An operation failed (with the exception and stack trace) | `Giving up on job …: Processor unreachable after 5 attempts` · `Processor rejected job …` · `Job failed: all 3 documents failed` · `Request failed: Storage unavailable` · `Dispatch loop iteration failed` · `Worker 2 crashed on job …` · `Failed to handle JobCompleted …; not acknowledging` · `Event outbox is full; dropping …` |
| **Critical** | The process is about to terminate | `Unhandled exception (process terminating: True)` |

Two deliberate choices:
- A **single failing document is a Warning**: the job continues and completes with that document marked Failed. A job where **every** document fails is an **Error**.
- A **client error (400/404/409) is not an Error**: the request log line is Information. Only failures of the service itself (5xx, storage down, a bug) are Errors. This keeps `level: "Error"` a list of things someone must act on.

### 1.4 Following one job across the services

- The **correlation id** comes from the `X-Correlation-Id` request header, or is generated, and is returned in the response. It is stored **with the job**.
- Background work for a job (dispatcher, WebSocket event handling, reconciler, audit publisher, processor worker) restores that id. Outgoing HTTP calls carry it as a header and WebSocket events carry it in the envelope.
- Inside job processing, a logging scope adds **`JobId`** to every line.

So the full story of a job, across all three services, is one query:

```javascript
db.logs.find({ jobId: "<job-id>" }, { _id: 0, timestamp: 1, service: 1, level: 1, message: 1 }).sort({ timestamp: 1 })
```

### 1.5 Other observability features

| Feature | Where |
|---|---|
| **Request logging**: one line per HTTP call (method, path, status, duration). Health probes excluded | `UseSerilogRequestLogging` in `UseServiceDefaults` |
| **Startup lines**: where logs go, then *Service started … listening on …*. If the second line is missing, startup failed | `ServiceLifecycleLogger` |
| **Unhandled exceptions** → Critical (process-wide) and unobserved task exceptions → Error | `ServiceLifecycleLogger` |
| **Health checks**: `/health/live` (process) and `/health/ready` (MongoDB; processor WebSocket link = Degraded) | All services; used by docker-compose health checks |
| **Correlation ids** sanitized (≤ 64 safe characters) so a caller cannot forge log lines | `Correlation.IsValid` |

## 2. Why this approach

### 2.1 Advantages

| Advantage | Explanation |
|---|---|
| **Meets both needs with one call site** | The code calls `ILogger<T>` once. Serilog sends the line to the console (operators, `docker logs`) and to MongoDB (history, search). Nothing in the business code knows where logs go |
| **Debug detail without noisy consoles** | Debug lines go only to MongoDB, so the console stays a readable flow even at 10 × 1,000 documents, while the detail needed after a failure is still kept |
| **Logging cannot hurt the service** | Bounded buffer, no blocking, no exceptions out of the sink, fail-fast writer. A slow or dead log database costs at most the stored history, never availability |
| **Crashes are never silent** | The console is synchronous (the last lines are in `docker logs`), and the Mongo buffer is flushed on an unhandled exception |
| **One place for three services** | A single collection with `service`, `jobId` and `correlationId` fields and indexes. One query shows the whole distributed flow |
| **Bounded and self-cleaning** | TTL index; capped message/exception sizes |
| **No new dependency** | Built on `MongoDB.Driver`, which the project already uses; fully unit-tested (including drop and failure paths) |

### 2.2 Alternatives considered

| Alternative | Why not chosen |
|---|---|
| `Serilog.Sinks.MongoDB` (community package) | A new dependency whose driver version must match ours, and less control over the outage behavior (blocking, retries, loss reporting). Our sink is about 200 lines and fully tested |
| Console only, with Docker collecting the logs | Does not meet the requirement to store logs in MongoDB; `docker logs` is lost when a container is removed and is hard to search across services |
| Log shipping stack (ELK / Loki / Seq / OpenTelemetry Collector) | The right production answer (§4), but heavy for the assignment's Docker Compose and time-box |
| Writing each log line synchronously to MongoDB | Every request and every document would wait for a database round-trip, and a database outage would block or crash the services |
| Same level for both destinations | Debug on the console is unreadable under load; Information only in MongoDB loses the detail needed to investigate |

## 3. Limitations and trade-offs

| Limitation | Impact | Mitigation |
|---|---|---|
| Logs are stored in the same MongoDB server as the data | A database outage loses log history for that time (the console still has it) | Separate log store in production (§4) |
| Records in the buffer (≤ ~1 s) are lost on a hard kill (`docker kill`, OOM) | Missing tail of Debug detail in MongoDB | Console is the source of truth for crashes; flushed on unhandled exceptions |
| Under an extreme burst (> 10,000 records waiting) records are dropped | Gaps in MongoDB, never in the console | Drops are counted and reported in the stored logs; buffer size configurable |
| No metrics or traces | Throughput, queue depth and latency are not charted | OpenTelemetry next (§4) |
| Log text is not scrubbed for sensitive data | Document *contents* are never logged by design (only names, counts and ids) | Keep it that way in code review |
| Changing levels needs a restart | No runtime level switch | Serilog `LoggingLevelSwitch` + admin endpoint if needed |

## 4. Possible improvements for production

- **OpenTelemetry**: traces across the three services (W3C trace context replaces our correlation header), plus metrics such as queue depth, dispatch latency, outbox size and documents per second.
- A dedicated log platform (Loki / Elasticsearch / Seq / a cloud log service) fed by the console JSON. The MongoDB sink stays useful for small deployments.
- Alerts on `level: Error/Critical` rates and on `log events were not stored`.
- Runtime level switching and per-job Debug sampling.

## 5. Tests for this section

The complete catalog of the system's tests, with strategy, environment, traceability and results, is the **Software Test Description (STD)** — `docs/testing/TA9-STD.docx` (Markdown: `docs/testing/STD.md`). This chapter lists only the tests that verify **§7 Logging & Observability**. The **STD ID** column is the identifier of the same test case in the STD, so each test can be looked up there; *Theory ×N* runs N data rows, *×2 stores* runs against InMemory and MongoDB.

### 5.1 What is verified

| Claim in this document | Verified by |
|---|---|
| Logging never blocks the caller; a full buffer drops and counts; losses are reported in the stored logs | `BatchingLogSinkTests` |
| Batches by size and time; everything pending is written on shutdown; a failed write is isolated and reported on stderr | `BatchingLogSinkTests`, `ServiceLoggingPipelineTests` |
| Stored record shape: level, plain message, service, host, `jobId` / `correlationId` promoted, sizes capped, safe BSON | `LogRecordTests` |
| Real pipeline: Debug + Information + Error stored with service and correlation id; Mongo level filter; disable switch; startup lines; unhandled errors with stack trace | `ServiceLoggingPipelineTests` |
| MongoDB: queryable by job id, 7-day TTL index, lookup indexes, retention change, 3 s fail-fast | `MongoLogBatchWriterTests` (Docker) |
| The right level for each event: flow = Information, detail = Debug, handled problem = Warning, failure = Error; job id on every line | `JobDispatcherLoggingTests`, `JobRunnerLoggingTests` |
| What an API call leaves in the store; client errors are not Errors; storage outage is an Error | `JobsApiLoggingTests`, `JobsApiStorageFailureTests`, `AuditApiLoggingTests` |
| Correlation id is generated, validated, propagated over HTTP and kept across awaits | `CorrelationIdMiddlewareTests`, `CorrelationIdHandlerTests`, `CorrelationTests` |
| Health endpoints | `JobsApiTests`, `AuditApiTests`, `ProcessApiTests` |
| **End to end: all three services log one job to MongoDB under one correlation id** | **E2E test below** and manual **MT-11** |

### 5.2 Automated tests

| STD ID | Test case | Test class | Level | Kind |
|---|---|---|---|---|
| SD-014 | Writes events in order in batches no larger than the batch size | `BatchingLogSinkTests` | Unit | Fact |
| SD-015 | Flushes a partial batch after the flush interval | `BatchingLogSinkTests` | Unit | Fact |
| SD-016 | Dispose writes pending events before returning | `BatchingLogSinkTests` | Unit | Fact |
| SD-017 | Full buffer drops events without blocking and reports the count | `BatchingLogSinkTests` | Unit | Fact |
| SD-018 | Failed write is reported and later events still reach the store | `BatchingLogSinkTests` | Unit | Fact |
| SD-019 | Emit after dispose is counted as dropped and does not throw | `BatchingLogSinkTests` | Unit | Fact |
| SD-020 | Maps level rendered message service host and exception | `LogRecordTests` | Unit | Fact |
| SD-021 | Renders string values without quotes and other values as serilog does | `LogRecordTests` | Unit | Fact |
| SD-022 | Promotes correlation and job ids and keeps other properties as text | `LogRecordTests` | Unit | Fact |
| SD-023 | Truncates oversized message and exception | `LogRecordTests` | Unit | Fact |
| SD-024 | Bson document has indexed fields and safe property names | `LogRecordTests` | Unit | Fact |
| SD-025 | Debug information and error are stored with service and correlation id | `ServiceLoggingPipelineTests` | Component | Fact |
| SD-026 | Mongo minimum level filters what is stored | `ServiceLoggingPipelineTests` | Component | Fact |
| SD-027 | Unhandled endpoint exception is stored as an error with the stack trace | `ServiceLoggingPipelineTests` | Component | Fact |
| SD-028 | Startup logs where logs go and that the service started | `ServiceLoggingPipelineTests` | Component | Fact |
| SD-029 | Disabled mongo logging stores nothing | `ServiceLoggingPipelineTests` | Component | Fact |
| SD-030 | Stopping the host flushes buffered records | `ServiceLoggingPipelineTests` | Component | Fact |
| SD-031 | Stores records that can be queried by job id | `MongoLogBatchWriterTests` | Component | Fact |
| SD-032 | Creates the retention ttl index and the lookup indexes | `MongoLogBatchWriterTests` | Component | Fact |
| SD-033 | Changing the retention updates the existing ttl index | `MongoLogBatchWriterTests` | Component | Fact |
| SD-034 | Unreachable database fails fast | `MongoLogBatchWriterTests` | Component | Fact |
| JS-146 | Accepted dispatch logs the flow at information and details at debug | `JobDispatcherLoggingTests` | Unit | Fact |
| JS-147 | Unreachable processor logs warnings then an error when giving up | `JobDispatcherLoggingTests` | Unit | Fact |
| JS-148 | Rejected payload is logged as an error | `JobDispatcherLoggingTests` | Unit | Fact |
| JS-149 | Unexpected failure is logged as an error with the exception | `JobDispatcherLoggingTests` | Unit | Fact |
| PS-105 | Successful job logs start and completion with the job id | `JobRunnerLoggingTests` | Unit | Fact |
| PS-106 | Failing document is a warning with the exception and the job still completes | `JobRunnerLoggingTests` | Unit | Fact |
| PS-107 | Job where every document fails is logged as an error | `JobRunnerLoggingTests` | Unit | Fact |
| JS-150 | Creating a job stores info and debug records with job id and correlation id | `JobsApiLoggingTests` | Component | Fact |
| JS-151 | Invalid request is logged without error level | `JobsApiLoggingTests` | Component | Fact |
| JS-152 | Storage outage is logged as an error with the correlation id | `JobsApiStorageFailureTests` | Component | Fact |
| AS-012 | Recording logs information and a duplicate logs debug | `AuditApiLoggingTests` | Component | Fact |
| SD-004 | Generates id when header missing and echoes it | `CorrelationIdMiddlewareTests` | Component | Fact |
| SD-005 | Reuses valid incoming id | `CorrelationIdMiddlewareTests` | Component | Fact |
| SD-006 | Replaces invalid incoming id | `CorrelationIdMiddlewareTests` | Component | Fact |
| SD-001 | Adds header from ambient scope | `CorrelationIdHandlerTests` | Unit | Fact |
| SD-002 | Does not add header without scope | `CorrelationIdHandlerTests` | Unit | Fact |
| SD-003 | Keeps explicit header | `CorrelationIdHandlerTests` | Unit | Fact |
| SD-007 | IsValid accepts safe ids | `CorrelationTests` | Unit | Theory ×3 |
| SD-008 | IsValid rejects unsafe ids | `CorrelationTests` | Unit | Theory ×6 |
| SD-009 | IsValid rejects ids longer than 64 chars | `CorrelationTests` | Unit | Fact |
| SD-010 | BeginScope sets and restores current id | `CorrelationTests` | Unit | Fact |
| SD-011 | Current id flows across awaits | `CorrelationTests` | Unit | Fact |
| JS-135 | Readiness reports storage health | `JobsApiTests` | Component | Fact |
| AS-005 | Readiness reports storage | `AuditApiTests` | Component | Fact |
| PS-104 | Liveness endpoint is healthy | `ProcessApiTests` | Component | Fact |
| E2E-006 | Job flow is logged to mongodb by all three services under one correlation id | `EndToEndTests` | Integration (E2E) | Fact |

Tests marked as requiring Docker run with `Category=Integration` (Testcontainers). The logging tests were added after the latest full run (407 / 407); re-run pending.

### 5.3 Manual tests

| STD ID | Procedure | Expected result | Status |
|---|---|---|---|
| MT-11 | After the smoke test (MT-02): `docker compose logs --tail 50 job-service processor-service`; then `docker compose exec mongo mongosh ta9_logs --eval "db.logs.find({jobId:'<id>'}).sort({timestamp:1})"` | Console shows the flow (Information); MongoDB holds Debug + Information of all three services for the job under one `correlationId`; index `ttl_timestamp` exists | Not yet executed |
| MT-09 | `docker compose stop mongo` while the stack runs; watch `docker compose logs job-service`; start it again | `Request failed: Storage unavailable` (Error) and `[ta9-log-sink] … could not store` on the console; after restart a stored Warning `N log events were not stored` | Not yet executed |

