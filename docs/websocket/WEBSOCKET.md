# WebSocket Disconnect / Reconnect Behavior — TA9 Document Processing System

| | |
|---|---|
| **Assignment sections** | §3.3 WebSocket Events · §5 Reliability ("For WebSocket disconnections and duplicate events, choose a sensible behavior and document it") · §8 README item "WebSocket disconnect/reconnect behavior" |
| **Link** | Processor Service (WebSocket **client**) → Job Service endpoint `ws://<job-service>/ws/processor-events` (WebSocket **server**) |
| **Chosen behavior** | **At-least-once delivery** of lifecycle events (bounded outbox + acknowledgements + automatic reconnect with exponential backoff and jitter) combined with **idempotent handling** on the receiver, plus a **reconciler** as a safety net. Progress events are best effort |
| **Diagrams** | draw.io pages **9. WebSocket — Connection & Outbox** and **5c. Sequence — WebSocket Reconnect** in `docs/hld/diagrams/ta9-hld.drawio` |

## 1. Context

The processor reports each job's lifecycle to the Job Service over one persistent WebSocket connection:

| Event | Payload | Importance |
|---|---|---|
| `JobStarted` | total documents | lifecycle: must arrive |
| `JobProgress` | processed / total documents | informational: may be skipped |
| `JobCompleted` | summary + results of every document | lifecycle: must arrive |
| `JobFailed` | error code + reason | lifecycle: must arrive |
| `JobCancelled` | — | lifecycle: must arrive |

Every message is a JSON envelope `{ schemaVersion, eventId, type, jobId, occurredAt, correlationId, payload }`. The Job Service answers each handled event with `{ "type": "Ack", "eventId": "…" }`.

A WebSocket can break at any time: network failure, Job Service restart or deployment, processor restart, or a proxy closing idle connections. A successful `SendAsync` only means the bytes reached the local TCP buffer; it proves nothing about whether the Job Service received or stored the event. The design must therefore answer three questions: **what is resent, when, and what happens when something arrives twice**.

## 2. What we implemented

![WebSocket link — connection, outbox and receiver behavior](../hld/images/websocket-states.png)

### 2.1 Processor side: connection management (`JobServiceEventsClient`)

A background service keeps exactly one connection open and repairs it when it breaks (diagram part A).

| Behavior | Detail |
|---|---|
| **Connect** | `ClientWebSocket.ConnectAsync` with a 10 s timeout; header `X-Processor-Id` identifies the instance in the Job Service logs |
| **Keep-alive** | WebSocket ping every 15 s on both sides, so idle connections are not silently dropped by proxies and dead peers are detected |
| **Two loops per connection** | one **send loop** (the only writer: `WebSocket` allows one concurrent send) and one **receive loop** that reads acks. Whichever stops first ends the connection; the other is cancelled, the socket is closed gracefully and then aborted, so no loop can hang |
| **Wake-up** | the send loop sleeps until a new event is added to the outbox, or every 5 s to resend events whose ack timed out. No busy polling |
| **Reconnect** | on any failure: exponential backoff **1 s → 2 s → 4 s … capped at 30 s, ±20% jitter**. The attempt counter resets after a successful connect. The jitter keeps many processors from reconnecting in lockstep (thundering herd) |
| **On (re)connect** | `MarkConnected()` makes **every unacknowledged lifecycle event due again**: they are replayed in their original order before new events |
| **On disconnect** | `MarkDisconnected()` keeps lifecycle events and **drops pending progress** (stale by the time the link is back) |
| **Health** | `/health/ready` reports **Degraded** (HTTP 200) while disconnected: the processor keeps accepting and processing work, and its events are buffered |
| **Shutdown** | graceful close of the socket; the background service stops promptly |

### 2.2 Processor side: what is kept and resent (`EventOutbox`)

Diagram part B.

| Event kind | Behavior |
|---|---|
| **Lifecycle** (`JobStarted`, `JobCompleted`, `JobFailed`, `JobCancelled`) | Added to an ordered, bounded outbox (10,000 events). States: **Pending → Sent → Acknowledged (removed)**. A *Sent* event becomes *Pending* again on reconnect, or when no ack arrives within **30 s** (ack timeout), even without a disconnect |
| **Progress** (`JobProgress`) | Never stored for retry: **coalesced** to the latest value per job, **dropped while disconnected**, sent once, never acknowledged. A terminal event discards the job's pending progress |
| **Outbox full** | The new lifecycle event is dropped with an error log; the publisher never blocks or throws. The Job Service reconciler recovers the job's state (§2.5) |

### 2.3 Job Service side: receiving and acknowledging (`ProcessorEventsEndpoint`)

Diagram part C.

| Behavior | Detail |
|---|---|
| **One sequential loop per connection** | events of one processor are handled one at a time, **in the order they were sent**, with no locks |
| **Ack after handling** | the ack is sent only after the event has been persisted, or deliberately ignored. This is what makes the processor's outbox meaningful |
| **Ignored events are acked too** | duplicates, late events, unknown jobs, unknown event types, newer schema versions and malformed payloads can never change state; acking them stops useless resends |
| **Storage failure → no ack** | if MongoDB is unavailable the handler throws and the event is **not** acknowledged; the processor resends it after the ack timeout. The connection stays open |
| **Unreadable message** | no `eventId` to acknowledge → skipped; the connection keeps working |
| **Protection** | messages above 16 MB close the connection (`MessageTooBig`); a `JobCompleted` for 1,000 documents is about 0.5 MB |
| **Shutdown / client gone** | the loop ends on the request-aborted or application-stopping token; the processor reconnects to the next instance |

### 2.4 Duplicates and ordering: idempotent handling (`ProcessorEventHandler`)

At-least-once delivery means an event can arrive more than once, for example when the ack was lost or the ack timeout fired while the event was being stored. Every event is therefore safe to apply twice:

| Mechanism | Effect |
|---|---|
| **Job state machine** | only legal transitions are applied: `JobStarted` after `Completed` and anything after `Cancelled` are no-ops |
| **Monotonic progress** | `processedDocuments = max(current, incoming)`: an older progress value never moves it backwards |
| **Idempotent results** | results are upserted by `(jobId, index)`; a replayed `JobCompleted` finds the job already terminal and changes nothing |
| **Optimistic concurrency** | every write checks the job version, so a replay racing with the dispatcher, reconciler or a cancel can never overwrite a newer state |
| **Pending race** | an event for a job still `Pending` (the processor finished before the dispatcher recorded its 202) first moves it to `Queued`, then applies |
| **Schema version** | an envelope with a newer `schemaVersion` is acked and ignored (forward compatible) |

**Result: at-least-once delivery + idempotent handling = every state change is applied exactly once.**

### 2.5 Safety net: the reconciler

The outbox lives in the processor's memory. It cannot survive a processor crash, and it can overflow during a very long outage. The Job Service's **reconciler** covers both cases. Every 30 s it looks for `Queued` / `Processing` jobs with no update for 60 s and asks the processor via `GET /api/process/{id}`:
- **Still running:** refresh progress and liveness.
- **Finished:** apply the result through the same handler.
- **Unknown (404, processor restarted):** move the job back to `Pending` and redispatch it (at most 3 times, then `Failed`).

Lost lifecycle events are therefore recovered within about 60–90 s.

### 2.6 Scenario walkthrough

| Scenario | What happens | Outcome |
|---|---|---|
| Short network drop | Processor detects the closed socket, backs off 1 s, reconnects, replays unacked events | No event lost; duplicates ignored |
| Job Service restart / redeploy | Same as above; the reconnect delay grows up to 30 s while the service is down; processing continues and events accumulate in the outbox | Everything is delivered after restart |
| Ack lost (event stored, ack not received) | Event is resent on reconnect or after 30 s; handler recognizes it as a duplicate and acks | State unchanged, outbox cleared |
| MongoDB down while events arrive | Handler throws, no ack; processor resends every 30 s | Applied as soon as MongoDB is back |
| Processor crash | Outbox and in-flight work are lost; reconciler finds the job missing (404) and redispatches it | Job completes on the restarted processor |
| Very long outage (outbox full) | New lifecycle events are dropped and logged; the reconciler later pulls each job's status | Jobs converge to the processor's state |
| Malformed message | Skipped (no `eventId`) or acked and ignored (bad payload) | Connection keeps working |
| Out-of-order delivery (e.g. replay after a newer event) | State machine and monotonic progress reject regressions | No state goes backwards |

![Sequence — WebSocket disconnect, reconnect and replay](../hld/images/sequence-websocket-reconnect.png)

## 3. Why this approach

### 3.1 Advantages

| # | Advantage | Explanation |
|---|---|---|
| A1 | **No lost lifecycle events across disconnects** | Acks tell the processor exactly what the Job Service stored; everything else is resent. TCP success alone could not provide this. |
| A2 | **Duplicates are harmless by construction** | Idempotency comes from the job state itself (state machine, monotonic progress, upserts, versioning), so no deduplication table or sequence store is needed. |
| A3 | **Survives restarts on either side** | Job Service restart → replay from the outbox; processor restart → reconciler. Neither requires manual intervention. |
| A4 | **Ordered, lock-free handling** | One sequential loop per connection preserves the sender's order without locking. |
| A5 | **Bounded resources** | The outbox is bounded (10,000), progress is coalesced per job, messages are capped at 16 MB, and reconnect delay is capped at 30 s. A long outage cannot exhaust memory or flood the network. |
| A6 | **Graceful degradation** | While disconnected the processor keeps working and reports Degraded rather than Unhealthy, so orchestration does not kill or stop routing to a healthy worker. |
| A7 | **No reconnect storms** | Exponential backoff with jitter spreads reconnects of many processors. |
| A8 | **Correct under storage outages** | "No ack on failure" turns a MongoDB outage into a delay, never into data loss. |
| A9 | **Forward compatible** | The schema version in the envelope lets a newer processor talk to an older Job Service without breaking it. |
| A10 | **Simple, dependency-free, testable** | Plain `ClientWebSocket` / ASP.NET Core WebSockets and a small outbox class. Every rule is covered by unit tests and real-WebSocket tests (§5). |

### 3.2 Alternatives considered

| Alternative | Why not |
|---|---|
| **Fire-and-forget** (send, no ack, no resend) | Events sent just before a disconnect are silently lost; jobs would stay `Processing` until a timeout. |
| **Reconnect only, rely on the reconciler** | Correct but slow: every disconnect would delay status updates by 60–90 s. The outbox makes the common case immediate; the reconciler stays as the rare-case net. |
| **Durable outbox in the processor (database)** | Survives processor crashes, but adds a database to a stateless worker and a second source of truth. The reconciler already covers crashes. |
| **Deduplication table by `eventId`** | Extra storage and cleanup; unnecessary because handlers are idempotent by design. |
| **Per-job sequence numbers** | Strict ordering per job, but adds sequence tracking on both sides; monotonic progress and the state machine already reject regressions. |
| **SignalR** | Automatic reconnect, but no delivery guarantee for messages sent while disconnected, plus an extra protocol and dependency. Acks and replay would still be needed. |
| **Message broker** (RabbitMQ/Kafka) | Durable, ordered delivery: the right choice at scale, but explicitly out of scope, and the assignment asks for WebSocket communication. |
| **HTTP callbacks instead of WebSocket** | Simpler retries, but one request per event and no persistent channel, which goes against the assignment's WebSocket requirement. |

## 4. Limitations

| # | Limitation | Impact | Mitigation / next step |
|---|---|---|---|
| L1 | **Outbox is in memory** | A processor crash loses its unacknowledged events | Reconciler recovers within ~60–90 s; a durable outbox or broker removes the gap |
| L2 | **Bounded outbox drops when full** | After ~10,000 unacked lifecycle events (a very long outage) new events are dropped | Logged as errors; reconciler recovers; capacity is configurable |
| L3 | **Duplicates do reach the receiver** | Extra handler work and log lines on replays | Handlers are idempotent; cost is a version check per duplicate |
| L4 | **Progress may be skipped** | Clients can miss intermediate progress values during a disconnect | By design: the next progress or the terminal event carries the latest state |
| L5 | **Up to ~30 s added latency after an ack loss** | An event whose ack was lost is resent only on reconnect or after the 30 s ack timeout | Ack timeout is configurable; normal operation is unaffected |
| L6 | **One connection per processor, one Job Service instance** | All events of a processor flow through one socket into one instance | Multi-instance Job Service would need a load balancer with any-instance handling (already idempotent) or a broker |
| L7 | **No authentication on the WebSocket** | Any client that can reach the endpoint could send events | Out of scope by assignment; production: mTLS or a signed token on connect |
| L8 | **Large completion messages** | ~0.5 MB per 1,000-document `JobCompleted`; a replay resends the whole payload | Fine at this scale; for larger jobs send a summary and let the Job Service pull results via REST |

## 5. Tests

### 5.1 Automated tests

| Test class (project) | Level | What it verifies |
|---|---|---|
| `EventOutboxTests` (Processor) | Unit | Rejects lifecycle events beyond capacity · Takes unsent events in order only once · Resends events whose ack timed out · Acknowledge removes the event · Reconnect makes unacknowledged events due again · Lifecycle events are buffered while disconnected · Progress is dropped while disconnected · Progress is coalesced to the latest per job and not kept for ack · Terminal event discards pending progress of the job · Disconnect discards pending progress · Wait for work completes when an event is added |
| `JobServiceEventsClientTests` (Processor) | Component — **real WebSocket** against an in-memory fake Job Service | Delivers events in order and clears outbox on ack · Buffers events until the Job Service becomes reachable · **Replays unacknowledged events after reconnect** · **Resends when ack times out without reconnect** · Sends progress while connected without waiting for ack |
| `ProcessorEventSerializerTests` (Processor) | Unit | Envelope round trips with typed payload · camelCase and string enums on the wire · Ack round trips · Non-ack messages are ignored |
| `WebSocketJobEventPublisherTests` (Processor) | Unit | Lifecycle events queued with job metadata · Completed event carries summary and results · Progress dropped while disconnected · Full outbox does not throw |
| `ExponentialBackoffTests` (ServiceDefaults) | Unit | Exponential growth up to the cap with ±20% jitter · Jitter spreads retries |
| `ProcessorEventsEndpointTests` (Job Service) | Component — **real WebSocket** against the endpoint | Handled events are acknowledged and applied · **Duplicates and unknown jobs are acknowledged too** · Unreadable message is skipped and the connection keeps working · **Events are processed in order per connection** · Plain HTTP request is rejected · **Event that cannot be stored is not acknowledged** |
| `ProcessorEventHandlerTests` (Job Service) | Unit | Started moves a queued job to processing · Events arriving before the dispatcher recorded the 202 still apply · Progress never goes backwards · Completed stores results then completes the job · **Replayed completion is ignored** · Late completion of a cancelled job is ignored · **Out-of-order Started after Completed is ignored** · Unknown job / event type / newer schema / malformed payload ignored · Storage failure propagates (no ack) |
| `JobReconcilerTests` (Job Service) | Unit | **Lost completion / failure events are recovered** · Job lost by a restarted processor is redispatched · Fails after too many redispatches · Unreachable processor leaves the job alone · Event arriving during reconciliation wins over redispatch |
| `EndToEndTests` (Integration) | E2E — three services, real WebSocket, MongoDB | Job flows through the processor WebSocket and audit · **Job lost by a processor restart is redispatched and completes** (old instance dies mid-job, new instance reconnects its WebSocket) |

All of these tests pass in the latest run (404 / 404 automated tests passed).

### 5.2 Manual verification (Docker Desktop)

| Step | Command | Expected |
|---|---|---|
| 1 | Start the stack: `docker compose up -d` (or `scripts/docker-up-prebuilt.ps1`) | All containers healthy |
| 2 | Start a long job: `powershell -ExecutionPolicy Bypass -File .\scripts\smoke-test.ps1 -Jobs 10 -Documents 1000` | Jobs start processing |
| 3 | `docker compose restart job-service` while jobs run | — |
| 4 | `docker compose logs -f processor-service` | `Job Service events connection failed (attempt N)` with growing delays, then `Connected to Job Service events endpoint … N events pending` |
| 5 | `GET /api/jobs?status=Completed` | Every job Completed; nothing lost |
| 6 | `curl.exe http://localhost:5002/health/ready` during step 3 | `Degraded` while disconnected, `Healthy` after reconnect |

This is procedure **MT-07** in the STD.

## 6. Configuration

| Setting (environment variable) | Default | Meaning |
|---|---|---|
| `JobServiceEvents__Url` | `ws://localhost:5001/ws/processor-events` | Job Service WebSocket endpoint |
| `JobServiceEvents__OutboxCapacity` | 10000 | Max lifecycle events waiting for an ack |
| `JobServiceEvents__AckTimeout` | 00:00:30 | Resend a sent event if no ack arrives in this time |
| `JobServiceEvents__ResendCheckInterval` | 00:00:05 | How often the send loop wakes up to check ack timeouts |
| `JobServiceEvents__ReconnectInitialDelay` | 00:00:01 | First reconnect delay |
| `JobServiceEvents__ReconnectMaxDelay` | 00:00:30 | Reconnect delay cap |
| `JobServiceEvents__ConnectTimeout` | 00:00:10 | Connect attempt timeout |
| `JobServiceEvents__KeepAliveInterval` | 00:00:15 | Client ping interval (the server also pings every 15 s) |
| `Reconcile__StaleAfter` / `Reconcile__Interval` | 60 s / 30 s | Safety net for events lost despite the outbox |
