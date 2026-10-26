# 02 — API & Contracts

All REST errors use RFC 7807 `ProblemDetails`. Every request accepts or generates an `X-Correlation-Id` header, which is propagated on outgoing HTTP calls and in WS events.

## 1. Job Service (`:5001`)

| Method | Path | Description | Responses |
|---|---|---|---|
| POST | `/api/jobs` | Create a job | 201 + `Location`, 400, 413 |
| GET | `/api/jobs?status=&page=&pageSize=` | List job summaries (newest first) | 200 |
| GET | `/api/jobs/{id}` | Job details: status, progress, summary, timestamps, failure reason | 200, 404 |
| GET | `/api/jobs/{id}/results?page=&pageSize=` | Per-document results (paged, max 200 per page) | 200, 404, 409 (not completed) |
| GET | `/api/jobs/{id}/events` | SSE stream of status/progress. Sends a snapshot first and closes on a terminal state | 200 `text/event-stream`, 404 |
| POST | `/api/jobs/{id}/cancel` | Cancel a Pending/Queued/Processing job | 202, 404, 409 (already terminal) |
| DELETE | `/api/jobs/{id}` | Delete a **terminal** job and its documents | 204, 404, 409 (still active) |
| WS | `/ws/processor-events` | Internal: processor → job service event channel | — |
| GET | `/health/live`, `/health/ready` | Liveness / readiness (Mongo) | 200 / 503 |

There is **no PUT**: a job is immutable after it is submitted. To change it, cancel it and submit a new one.

### Create job

```json
POST /api/jobs
{
  "name": "quarterly-reports",               // optional, ≤ 200 chars
  "documents": [
    { "name": "a.txt", "content": "Hello world hello" }
  ]
}
```

**Validation (400):** `documents` is required, with 1..1000 items. `content` is required (empty or whitespace is allowed and yields 0 words) and limited to 100 KB UTF-8. A document `name` is ≤ 256 chars. The whole body is limited to 10 MB by Kestrel and returns **413** above that.

```json
201 Created   Location: /api/jobs/6f1c…
{ "id": "6f1c…", "status": "Pending", "totalDocuments": 1, "createdAt": "…" }
```

### Job details

```json
{
  "id": "6f1c…", "name": "quarterly-reports", "status": "Completed",
  "totalDocuments": 1000, "processedDocuments": 1000,
  "summary": { "succeeded": 998, "failed": 2, "totalWords": 512345, "durationMs": 1840 },
  "failureReason": null,
  "createdAt": "…", "startedAt": "…", "completedAt": "…"
}
```

### Document result (one row of `/results`)

```json
{
  "index": 0, "name": "a.txt", "status": "Succeeded",
  "wordCount": 3, "uniqueWordCount": 2,
  "topWords": [ { "word": "hello", "count": 2 }, { "word": "world", "count": 1 } ],
  "sha256": "a1b2…",
  "characterCount": 17, "lineCount": 1, "averageWordLength": 5.0,
  "error": null
}
```

### SSE

Each event's `data` is the same `JobResponse` as `GET /api/jobs/{id}`. The `id` is the job version, which is monotonic. A `: keep-alive` comment is sent every 15s while idle, and the stream ends after the terminal state.

```
id: 3
event: status
data: {"id":"…","status":"Processing","processedDocuments":300,"totalDocuments":1000,"progressPercent":30.0,…}

: keep-alive

id: 7
event: status
data: {"id":"…","status":"Completed",…}
```

**Implementation:** a repository decorator publishes every successful job write to an in-process broadcaster. Each subscriber has a bounded (16) drop-oldest buffer, so a slow client never blocks writers and always ends with the latest snapshot. The endpoint subscribes **before** reading the snapshot and skips updates whose version is not newer, so nothing is missed or duplicated.

## 2. Processor Service (`:5002`)

| Method | Path | Description | Responses |
|---|---|---|---|
| POST | `/api/process` | Enqueue a job. **Idempotent by `jobId`**: a known jobId returns its current state | 202 (new), 200 (already known), 400, 503 + `Retry-After` (queue full) |
| GET | `/api/process/{jobId}` | Status, plus results once terminal (kept in memory for `ResultsTtl`, default 1h) | 200, 404 |
| GET | `/api/process` | List known jobs (diagnostics) | 200 |
| DELETE | `/api/process/{jobId}` | Cancel (cooperative `CancellationToken`) | 202, 404 |
| GET | `/health/live`, `/health/ready` | Ready reports **Degraded** (still HTTP 200) while the WS connection is down, because work is still accepted and events are buffered | 200 / 503 |

```json
POST /api/process
{ "jobId": "6f1c…", "documents": [ { "index": 0, "name": "a.txt", "content": "…" } ] }
```

## 3. WebSocket event contract (Processor → Job Service)

**Direction:** the Processor is the WS **client** and connects to `ws://job-service/ws/processor-events`. This matches the diagram, where the Job Service exposes the WebSocket endpoint. Messages are JSON text frames.

### Envelope

```json
{
  "schemaVersion": 1,
  "eventId": "b7e0…",            // unique per event; used for ack
  "type": "JobStarted",          // JobStarted | JobProgress | JobCompleted | JobFailed | JobCancelled
  "jobId": "6f1c…",
  "occurredAt": "2026-10-26T10:00:00Z",
  "correlationId": "…",
  "payload": { }
}
```

| type | payload | delivery |
|---|---|---|
| `JobStarted` | `{ "totalDocuments": 1000 }` | at-least-once (outbox + ack) |
| `JobProgress` | `{ "processedDocuments": 300, "totalDocuments": 1000 }` | **best-effort**: throttled (≥10% step or ≥2s), coalesced to the latest per job, dropped while disconnected, never replayed |
| `JobCompleted` | `{ "summary": {…}, "results": [ DocumentResult… ] }` | at-least-once |
| `JobFailed` | `{ "errorCode": "ProcessingError", "reason": "…" }` | at-least-once |
| `JobCancelled` | `{}` | at-least-once |

### Ack (Job Service → Processor)

```json
{ "type": "Ack", "eventId": "b7e0…" }
```

The Job Service acks **after** it has handled the event, whether it persisted it or deliberately ignored it as a duplicate or illegal transition. The Processor removes the event from its outbox only on ack. Anything unacked is resent after reconnect.

### Receiver rules (Job Service)
1. Unknown `jobId`: log a warning and ack (nothing to retry).
2. Illegal transition (e.g. `JobStarted` after `Completed`, anything after `Cancelled`): ignore, log at debug, ack.
3. `JobProgress`: `processedDocuments = max(current, incoming)`, so out-of-order progress is harmless. It also implies `Queued → Processing`.
4. `JobCompleted`: bulk-upsert results keyed by `(jobId, index)`, then transition. This is idempotent, so a replay is a no-op.
5. Unknown `type` or a newer `schemaVersion`: log, ack, ignore (forward compatible).
6. Any event for a job still `Pending`: first move it to `Queued`, because an event proves the Processor accepted it. This covers a small job that finishes before the dispatcher has recorded the 202.
7. Unparsable message: no ack (there is no eventId to ack). Storage failure while handling: **no ack**, so the Processor resends after its ack timeout. Malformed payload with a valid envelope: ack and ignore, since a retry would fail the same way.

## 4. Audit Service (`:5003`, bonus)

| Method | Path | Description | Responses |
|---|---|---|---|
| POST | `/api/audit` | Store a terminal job summary. Idempotent: the **first write wins** (audit records are immutable), and a repeat returns the original entry | 201 (new), 200 (existing), 400 |
| GET | `/api/audit/{jobId}` | Get the summary | 200, 404 |

```json
{ "jobId": "…", "name": "…", "status": "Completed", "totalDocuments": 1000,
  "succeededDocuments": 998, "failedDocuments": 2, "totalWords": 512345, "failureReason": null,
  "createdAt": "…", "completedAt": "…" }
```

The response adds `recordedAt`. **Delivery:** the Job Service's `AuditPublisher` polls terminal jobs with `auditPublished = false` (the flag is the outbox), posts them, and sets the flag. When the Audit Service is unavailable it stops the batch and backs off exponentially (5s up to 60s). A 4xx response is logged and not retried. Job state is never affected.
