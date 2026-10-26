"""Writes the 'Tests for this section' chapter of the topic documents, using the test IDs of the STD.

The STD (docs/testing/STD.md) holds the full catalog; each topic document lists only the subset that verifies
its assignment section, with the same IDs, so a reader can jump between the documents.

    python section-tests.py            (run from tools/docs, after regenerating the STD)
"""
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
STD = ROOT / "docs/testing/STD.md"

LEVEL = {
    "Unit": "Unit", "Component": "Component", "Contract": "Contract (InMemory + MongoDB)", "Integration (E2E)": "Integration (E2E)",
}


def parse_std():
    catalog, cls, level = {}, None, None
    for line in STD.read_text(encoding="utf-8").splitlines():
        if line.startswith("#### "):
            cls = line[5:].strip()
        elif line.startswith("**Level:**") and cls:
            level = re.match(r"\*\*Level:\*\* ([^·]+)", line).group(1).strip()
        else:
            m = re.match(r"^\| ((?:SD|PS|JS|AS|E2E)-\d{3}) \| (.+?) \| (.+?) \|$", line)
            if m and cls:
                catalog[(cls, m.group(2))] = (m.group(1), level, m.group(3))
    return catalog


def human(name):
    s = name.replace("_", " ")
    return s[0].upper() + s[1:]


def table(catalog, selection):
    rows = ["| STD ID | Test case | Test class | Level | Kind |", "|---|---|---|---|---|"]
    for cls, methods in selection:
        for m in methods:
            key = (cls, human(m))
            if key not in catalog:
                raise SystemExit(f"not in STD: {cls}.{m}")
            tid, level, kind = catalog[key]
            rows.append(f"| {tid} | {human(m)} | `{cls}` | {LEVEL.get(level, level)} | {kind} |")
    return "\n".join(rows)


def replace_section(path, start_heading, end_heading, new_text):
    text = path.read_text(encoding="utf-8")
    i = text.index(start_heading)
    j = text.index(end_heading, i) if end_heading else len(text)
    path.write_text(text[:i] + new_text + ("\n" + text[j:] if end_heading else "\n"), encoding="utf-8")


INTRO = ("The complete catalog of the system's tests, with strategy, environment, traceability and results, is the "
         "**Software Test Description (STD)** — `docs/testing/TA9-STD.docx` (Markdown: `docs/testing/STD.md`). "
         "This chapter lists only the tests that verify **{what}**. The **STD ID** column is the identifier of the same test case in the STD, "
         "so each test can be looked up there; *Theory ×N* runs N data rows, *×2 stores* runs against InMemory and MongoDB.")

catalog = parse_std()

# ------------------------------------------------------------------ §4 Concurrency & Performance
CONCURRENCY = [
    ("JobRunnerTests", ["Never_exceeds_document_parallelism", "Publishes_throttled_progress", "Isolates_a_failing_document",
                        "Stops_when_cancelled_mid_run_without_reporting_completion", "Abandons_job_silently_on_shutdown"]),
    ("JobWorkerServiceTests", ["Processes_many_jobs_with_bounded_total_concurrency", "A_crashing_job_does_not_kill_the_worker", "Stops_cleanly"]),
    ("ChannelJobQueueTests", ["Rejects_when_capacity_is_reached", "Delivers_jobs_in_fifo_order_and_frees_capacity"]),
    ("ProcessingServiceTests", ["Rejects_when_queue_is_full_and_forgets_the_job", "Resubmitting_the_same_job_id_is_idempotent"]),
    ("ProgressThrottleTests", ["Reports_every_ten_percent_but_not_at_completion", "Reports_when_the_interval_elapses_between_steps",
                               "Out_of_order_counts_do_not_produce_duplicate_reports"]),
    ("ProcessingJobTests", ["Cancel_moves_to_cancelled_and_signals_the_token", "Completion_after_cancel_is_ignored",
                            "Completes_only_from_processing_and_releases_documents"]),
    ("InMemoryProcessingJobStoreTests", ["Removes_only_finished_jobs_older_than_cutoff"]),
    ("ProcessApiTests", ["Post_returns_503_with_retry_after_when_queue_is_full", "Post_same_job_twice_returns_200_the_second_time",
                         "Delete_cancels_a_running_job"]),
    ("HttpProcessorClientTests", ["Submit_503_is_busy_with_the_retry_after_hint"]),
    ("JobDispatcherTests", ["Busy_processor_delays_the_job_without_consuming_an_attempt", "Dispatches_in_bounded_parallel_batches",
                            "A_crashed_dispatch_is_retried_after_the_lease_expires", "Background_loop_dispatches_new_jobs_when_signalled"]),
    ("JobRepositoryContract", ["Concurrent_updates_never_lose_a_write", "Update_with_a_stale_version_is_rejected",
                               "Concurrent_claims_never_hand_out_the_same_job_twice", "Claim_leases_the_due_pending_job_until_the_lease_expires"]),
    ("JobRepositoryExtensionsTests", ["Reloads_and_retries_on_conflict", "Gives_up_after_max_attempts"]),
    ("ProcessorEventsEndpointTests", ["Events_are_processed_in_order_per_connection"]),
    ("InMemoryJobUpdateBroadcasterTests", ["Slow_subscriber_keeps_the_latest_snapshots_and_never_blocks_the_publisher"]),
    ("EndToEndTests", ["Ten_concurrent_jobs_of_1000_documents_complete_with_bounded_parallelism"]),
]
conc = f"""## 7. Tests for this section

{INTRO.format(what='§4 Concurrency & Performance')}

### 7.1 What is verified

| Claim in this document | Verified by (STD IDs below) |
|---|---|
| Documents of one job never exceed `DocParallelismPerJob` | `JobRunnerTests.Never_exceeds_document_parallelism` |
| Total concurrent analyses never exceed `MaxConcurrentJobs × DocParallelismPerJob` | `JobWorkerServiceTests.Processes_many_jobs_with_bounded_total_concurrency`; E2E with 10 × 1,000 documents (≤ 20) |
| The queue is bounded; overload returns 503 + Retry-After and is honored without counting a failure | `ChannelJobQueueTests`, `ProcessingServiceTests`, `ProcessApiTests`, `HttpProcessorClientTests`, `JobDispatcherTests.Busy_processor…` |
| No lost updates between concurrent writers; a job is never dispatched twice at once | `JobRepositoryContract` (InMemory + MongoDB), `JobRepositoryExtensionsTests` |
| Failures, cancellation and shutdown never break the worker pool | `JobRunnerTests`, `JobWorkerServiceTests`, `ProcessingJobTests` |
| Slow consumers never block producers; per-connection ordering | `InMemoryJobUpdateBroadcasterTests`, `ProcessorEventsEndpointTests` |
| **The assignment scenario: 10 concurrent jobs × 1,000 documents** | **E2E test below** and manual **MT-03** |

### 7.2 Automated tests

{table(catalog, CONCURRENCY)}

### 7.3 Manual test

| STD ID | Procedure | Expected result | Status |
|---|---|---|---|
| MT-03 | `powershell -ExecutionPolicy Bypass -File .\\scripts\\smoke-test.ps1 -Jobs 10 -Documents 1000` against the Docker Desktop stack | All 10 jobs Completed, 1,000 / 1,000 documents succeeded each | Passed (2026-10-26) |

All automated tests listed here passed in the latest full run (440 / 440, 2026-10-27).
"""
replace_section(ROOT / "docs/concurrency/CONCURRENCY.md", "## 7. Tests for this section", None, conc)

# ------------------------------------------------------------------ §5 WebSocket disconnect / reconnect
WEBSOCKET = [
    ("EventOutboxTests", ["Rejects_lifecycle_events_beyond_capacity", "Takes_unsent_events_in_order_only_once", "Resends_events_whose_ack_timed_out",
                          "Acknowledge_removes_the_event", "Reconnect_makes_unacknowledged_events_due_again", "Lifecycle_events_are_buffered_while_disconnected",
                          "Progress_is_dropped_while_disconnected", "Progress_is_coalesced_to_the_latest_per_job_and_not_kept_for_ack",
                          "Terminal_event_discards_pending_progress_of_the_job", "Disconnect_discards_pending_progress",
                          "Wait_for_work_completes_when_an_event_is_added"]),
    ("JobServiceEventsClientTests", ["Delivers_events_in_order_and_clears_outbox_on_ack", "Buffers_events_until_the_job_service_becomes_reachable",
                                     "Replays_unacknowledged_events_after_reconnect", "Resends_when_ack_times_out_without_reconnect",
                                     "Sends_progress_while_connected_without_waiting_for_ack"]),
    ("ExponentialBackoffTests", ["Grows_exponentially_up_to_the_cap_with_jitter", "Jitter_spreads_retries"]),
    ("ProcessorEventSerializerTests", ["Envelope_round_trips_with_typed_payload", "Uses_camel_case_and_string_enums_on_the_wire",
                                       "Ack_round_trips", "Non_ack_messages_are_ignored"]),
    ("WebSocketJobEventPublisherTests", ["Lifecycle_events_are_queued_with_job_metadata", "Completed_event_carries_summary_and_results",
                                         "Progress_is_dropped_while_disconnected", "Full_outbox_does_not_throw"]),
    ("ProcessorEventsEndpointTests", ["Handled_events_are_acknowledged_and_applied", "Duplicates_and_unknown_jobs_are_acknowledged_too",
                                      "Unreadable_message_is_skipped_and_the_connection_keeps_working", "Events_are_processed_in_order_per_connection",
                                      "Plain_http_request_is_rejected", "Event_that_cannot_be_stored_is_not_acknowledged"]),
    ("ProcessorEventHandlerTests", ["Started_moves_a_queued_job_to_processing", "Events_arriving_before_the_dispatcher_recorded_the_202_still_apply",
                                    "Progress_is_recorded_and_never_goes_backwards", "Completed_stores_results_then_completes_the_job",
                                    "Replayed_completion_is_ignored", "Late_completion_of_a_cancelled_job_is_ignored_and_stores_no_results",
                                    "Out_of_order_started_after_completed_is_ignored", "Failed_records_code_and_reason", "Unknown_job_is_ignored",
                                    "Unknown_event_type_is_ignored", "Newer_schema_version_is_ignored", "Malformed_payload_is_ignored_not_retried",
                                    "Storage_failure_propagates_so_the_event_is_not_acknowledged"]),
    ("JobReconcilerTests", ["Lost_completion_event_is_recovered_with_results", "Lost_failure_event_is_recovered",
                            "Job_lost_by_a_restarted_processor_is_redispatched", "Job_lost_too_many_times_fails",
                            "Event_arriving_during_reconciliation_wins_over_redispatch"]),
    ("EndToEndTests", ["Job_flows_through_processor_websocket_and_audit", "Job_lost_by_a_processor_restart_is_redispatched_and_completes"]),
]
ws = f"""## 5. Tests for this section

{INTRO.format(what='the WebSocket disconnect / reconnect and duplicate-event behavior (§3.3 and §5 of the assignment)')}
Tests of the other §5 scenarios (invalid input, processor unreachable, storage outage) are traced in the STD under requirements **R8**, **R9** and **R14**.

### 5.1 What is verified

| Behavior described in this document | Verified by |
|---|---|
| Lifecycle events are kept until acked, resent after reconnect or ack timeout; progress coalesced/dropped | `EventOutboxTests`, `WebSocketJobEventPublisherTests` |
| Real reconnect over a WebSocket: buffering while unreachable, replay after a drop, resend on ack timeout | `JobServiceEventsClientTests` (real WebSocket against a fake Job Service) |
| Reconnect backoff grows exponentially with jitter | `ExponentialBackoffTests` |
| The wire contract (envelope, enums, ack) is stable | `ProcessorEventSerializerTests` |
| Ack only after handling; duplicates and unknown jobs acked; storage failure not acked; order per connection | `ProcessorEventsEndpointTests` (real WebSocket against the endpoint) |
| Duplicates, replays and out-of-order events never change state twice or move it backwards | `ProcessorEventHandlerTests` |
| Events lost despite the outbox are recovered by the reconciler | `JobReconcilerTests` |
| End to end, including a processor that dies mid-job and a new instance that reconnects | `EndToEndTests` |

### 5.2 Automated tests

{table(catalog, WEBSOCKET)}

All automated tests listed here passed in the latest full run (440 / 440, 2026-10-27).

### 5.3 Manual test (Docker Desktop) — STD MT-07

| Step | Command | Expected |
|---|---|---|
| 1 | Start the stack: `docker compose up -d` (or `scripts/docker-up-prebuilt.ps1`) | All containers healthy |
| 2 | Start a long run: `powershell -ExecutionPolicy Bypass -File .\\scripts\\smoke-test.ps1 -Jobs 10 -Documents 1000` | Jobs start processing |
| 3 | `docker compose restart job-service` while jobs run | — |
| 4 | `docker compose logs -f processor-service` | `Job Service events connection failed (attempt N)` with growing delays, then `Connected to Job Service events endpoint … N events pending` |
| 5 | `GET /api/jobs?status=Completed` | Every job Completed; nothing lost |
| 6 | `curl.exe http://localhost:5002/health/ready` during step 3 | `Degraded` while disconnected, `Healthy` after reconnect |

Status in the STD: *Not yet executed*.
"""
replace_section(ROOT / "docs/websocket/WEBSOCKET.md", "## 5. Tests", "## 6. Configuration", ws)

# ------------------------------------------------------------------ §6 Storage
def u(*names):
    return [n.replace(" ", "_") for n in names]


STORAGE = [
    ("JobRepositoryContract", u("Insert then get round trips every field", "Get unknown job returns null", "Update persists changes and increments version",
                                "Update with a stale version is rejected", "Changes to a loaded job are not visible until saved", "Concurrent updates never lose a write",
                                "List filters orders newest first and pages", "Claim leases the due pending job until the lease expires",
                                "Claim skips jobs that are not due or not pending", "Claim takes the longest waiting job first",
                                "Concurrent claims never hand out the same job twice", "Stale query returns only silent active jobs oldest first",
                                "Unpublished query returns finished jobs not yet audited", "Delete removes the job once")),
    ("JobDocumentRepositoryContract", u("Returns documents of one job ordered by index", "Saving results is idempotent", "Results are paged by index",
                                        "Results for unknown documents are ignored", "Delete removes only the given job")),
    ("AuditRepositoryContract", u("First write wins", "Unknown job returns null")),
    ("JobRepositoryExtensionsTests", u("Applies the mutation and saves", "Reports not found", "Does not write when the mutation declines",
                                       "Reloads and retries on conflict", "Gives up after max attempts")),
    ("BroadcastingJobRepositoryTests", u("Successful update is published with the new version", "Rejected update is not published",
                                         "Dispatch claims are not published")),
    ("InMemoryProcessingJobStoreTests", u("Rejects duplicate job id", "Removes only finished jobs older than cutoff")),
    ("MongoUnavailableTests", u("Job repository reads and writes report storage unavailable", "Failed update keeps the version token unchanged",
                                "Document repository reports storage unavailable")),
    ("JobsApiStorageFailureTests", u("Storage timeout returns 503 problem")),
    ("ProcessorEventsEndpointTests", u("Event that cannot be stored is not acknowledged")),
    ("ProcessorEventHandlerTests", u("Storage failure propagates so the event is not acknowledged")),
    ("JobsApiTests", u("Readiness reports storage health")),
    ("AuditApiTests", u("Readiness reports storage")),
]
storage = f"""## 7. Tests for this section

{INTRO.format(what='§6 Storage')}

### 7.1 What is verified

| Claim in this document | Verified by |
|---|---|
| InMemory and MongoDB stores behave identically (same suite, ×2 stores) | `JobRepositoryContract`, `JobDocumentRepositoryContract`, `AuditRepositoryContract` |
| Optimistic concurrency, atomic claim with lease, first write wins — on both stores | Concurrency cases of the contract suites |
| Callers are store-agnostic: load → mutate → save with retries | `JobRepositoryExtensionsTests` |
| A decorator adds behavior to any store | `BroadcastingJobRepositoryTests` |
| Processor work state is bounded in time (TTL eviction) | `InMemoryProcessingJobStoreTests` |
| Driver errors become `StorageUnavailableException`; version token kept on failure | `MongoUnavailableTests` (no Docker needed) |
| Storage outage → 503 on REST, no ack on WebSocket, readiness reflects storage | `JobsApiStorageFailureTests`, `ProcessorEventsEndpointTests`, `ProcessorEventHandlerTests`, `JobsApiTests`, `AuditApiTests` |
| Mongo GUID encoding defect stays fixed | Contract suites on MongoDB (STD defect **D-01**) |

### 7.2 Automated tests

{table(catalog, STORAGE)}

Contract suites against MongoDB need Docker (`Category=Integration`, Testcontainers). All tests listed here passed in the latest full run (440 / 440, 2026-10-27).

### 7.3 Manual tests

| STD ID | Procedure | Expected result | Status |
|---|---|---|---|
| MT-01 | `docker compose up -d`; open `/health/ready` on :5001 and :5003 | `Healthy` — MongoDB reachable | Passed (2026-10-26) |
| MT-09 | `docker compose stop mongo`; `GET /api/jobs` and `/health/ready` on :5001; `docker compose start mongo` | 503 *Storage unavailable* within ~5 s; readiness Unhealthy; recovers after Mongo starts | Not yet executed |
"""
replace_section(ROOT / "docs/storage/STORAGE.md", "## 7. Tests for this section", None, storage)

# ------------------------------------------------------------------ §7 Logging & Observability
LOGGING = [
    ("BatchingLogSinkTests", u("Writes events in order in batches no larger than the batch size", "Flushes a partial batch after the flush interval",
                               "Dispose writes pending events before returning", "Full buffer drops events without blocking and reports the count",
                               "Failed write is reported and later events still reach the store", "Emit after dispose is counted as dropped and does not throw")),
    ("LogRecordTests", u("Maps level rendered message service host and exception", "Renders string values without quotes and other values as serilog does",
                         "Promotes correlation and job ids and keeps other properties as text", "Truncates oversized message and exception",
                         "Bson document has indexed fields and safe property names")),
    ("ServiceLoggingPipelineTests", u("Debug information and error are stored with service and correlation id", "Mongo minimum level filters what is stored",
                                      "Unhandled endpoint exception is stored as an error with the stack trace", "Startup logs where logs go and that the service started",
                                      "Disabled mongo logging stores nothing", "Stopping the host flushes buffered records")),
    ("MongoLogBatchWriterTests", u("Stores records that can be queried by job id", "Creates the retention ttl index and the lookup indexes",
                                   "Changing the retention updates the existing ttl index", "Unreachable database fails fast")),
    ("JobDispatcherLoggingTests", u("Accepted dispatch logs the flow at information and details at debug", "Unreachable processor logs warnings then an error when giving up",
                                    "Rejected payload is logged as an error", "Unexpected failure is logged as an error with the exception")),
    ("JobRunnerLoggingTests", u("Successful job logs start and completion with the job id", "Failing document is a warning with the exception and the job still completes",
                                "Job where every document fails is logged as an error")),
    ("JobsApiLoggingTests", u("Creating a job stores info and debug records with job id and correlation id", "Invalid request is logged without error level")),
    ("JobsApiStorageFailureTests", u("Storage outage is logged as an error with the correlation id")),
    ("AuditApiLoggingTests", u("Recording logs information and a duplicate logs debug")),
    ("CorrelationIdMiddlewareTests", u("Generates id when header missing and echoes it", "Reuses valid incoming id", "Replaces invalid incoming id")),
    ("CorrelationIdHandlerTests", u("Adds header from ambient scope", "Does not add header without scope", "Keeps explicit header")),
    ("CorrelationTests", u("IsValid accepts safe ids", "IsValid rejects unsafe ids", "IsValid rejects ids longer than 64 chars",
                           "BeginScope sets and restores current id", "Current id flows across awaits")),
    ("JobsApiTests", u("Readiness reports storage health")),
    ("AuditApiTests", u("Readiness reports storage")),
    ("ProcessApiTests", u("Liveness endpoint is healthy")),
    ("EndToEndTests", u("Job flow is logged to mongodb by all three services under one correlation id")),
]
logging_md = f"""## 5. Tests for this section

{INTRO.format(what='§7 Logging & Observability')}

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

{table(catalog, LOGGING)}

Tests marked as requiring Docker run with `Category=Integration` (Testcontainers). All tests listed here passed in the latest full run (440 / 440, 2026-10-27).

### 5.3 Manual tests

| STD ID | Procedure | Expected result | Status |
|---|---|---|---|
| MT-11 | After the smoke test (MT-02): `docker compose logs --tail 50 job-service processor-service`; then `docker compose exec mongo mongosh ta9_logs --eval "db.logs.find({{jobId:'<id>'}}).sort({{timestamp:1}})"` | Console shows the flow (Information); MongoDB holds Debug + Information of all three services for the job under one `correlationId`; index `ttl_timestamp` exists | Not yet executed |
| MT-09 | `docker compose stop mongo` while the stack runs; watch `docker compose logs job-service`; start it again | `Request failed: Storage unavailable` (Error) and `[ta9-log-sink] … could not store` on the console; after restart a stored Warning `N log events were not stored` | Not yet executed |
"""
replace_section(ROOT / "docs/logging/LOGGING.md", "## 5. Tests for this section", None, logging_md)

print("concurrency + websocket + storage + logging test chapters written")
