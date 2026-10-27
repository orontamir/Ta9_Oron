# Installation Guide — TA9 Document Processing System

| | |
|---|---|
| **System** | Job Service (:5001) · Processor Service (:5002) · Audit Service (:5003) · MongoDB (:27017) · Web UI (:4200) |
| **Stack** | .NET 8 · ASP.NET Core · MongoDB 7 · Angular 17 + nginx (Web UI) · Docker / Docker Compose |
| **Repository** | https://github.com/orontamir/Ta9_OronTamir_HomeAssignment (branch **`dev`**) |
| **Related documents** | `README.md` (overview) · `docs/hld/TA9-HLD.docx` (High-Level Design) · `docs/logging/TA9-Logging-Observability.docx` (logs in detail) |
| **Using the system** | `docs/user-guide/TA9-User-Guide.docx`: Web UI, Swagger and Postman, step by step |
| **If something goes wrong** | **§5 Troubleshooting — where to look** (does not start · stuck · crashes) |

## 1. Overview

The system consists of three ASP.NET Core services, a MongoDB database and an optional web console:

| Component | Port | Purpose | Swagger |
|---|---|---|---|
| Job Service | 5001 | Public API `/api/jobs`, SSE stream, WebSocket endpoint for the processor | http://localhost:5001/swagger |
| Processor Service | 5002 | Internal API `/api/process`, document analysis | http://localhost:5002/swagger |
| Audit Service | 5003 | `/api/audit`, summaries of finished jobs | http://localhost:5003/swagger |
| MongoDB | 27017 | Storage for jobs, documents and audit entries; **logs of all services** (`ta9_logs.logs`) | — |
| **Web UI** | 4200 | Angular console over the public API: jobs list, create, live progress, results, cancel / delete, audit entry. nginx serves it and proxies `/api` to the services | **http://localhost:4200** |

There are three ways to run it:

| Option | When to use | Section |
|---|---|---|
| **Docker Desktop** (recommended) | Demo, review, closest to production | §4 |
| **Docker Desktop, prebuilt** | Networks with a TLS-inspecting proxy, where builds inside Docker cannot reach nuget.org or npm | §4.6 |
| **Local .NET** | Development and debugging | §3 |

## 2. Prerequisites

### 2.1 Hardware and operating system

| Item | Minimum |
|---|---|
| Operating system | Windows 10/11 (64-bit), macOS 12+, or Linux (x64/arm64) |
| CPU / RAM | 2 cores / 8 GB RAM (Docker Desktop should get at least 4 GB) |
| Disk | ~5 GB free (SDK, Docker images, MongoDB data) |
| Free ports | 5001, 5002, 5003, 27017, 4200 |

### 2.2 Software

| Software | Version | Required for | Download |
|---|---|---|---|
| **Git** | 2.40+ | Cloning the repository | https://git-scm.com/downloads |
| **Docker Desktop** | 4.x (Docker Engine 24+, Compose v2) | Running the stack; integration tests (Testcontainers) | https://www.docker.com/products/docker-desktop |
| **.NET 8 SDK** | 8.0.x | Local run, tests, prebuilt Docker path | https://dotnet.microsoft.com/download/dotnet/8.0 |
| **Node.js** *(optional)* | 20 or 24 LTS (includes npm) | Only for developing or testing the Web UI locally. Not needed for Docker | https://nodejs.org |
| PowerShell | 5.1+ (Windows) or 7+ | Helper scripts in `scripts/` | built in / https://aka.ms/powershell |
| Postman *(optional)* | any | The ready-made collection in `docs/postman` | https://www.postman.com/downloads |
| curl, jq *(optional)* | any | `scripts/smoke-test.sh`, SSE test | built in / package manager |

On **Windows**, Docker Desktop needs the **WSL 2** backend (Docker Desktop → Settings → General → *Use the WSL 2 based engine*). WSL 2 is installed automatically or with `wsl --install`.

Quick install on Windows with winget:

```powershell
winget install Git.Git
winget install Docker.DockerDesktop        # restart Windows when asked
winget install Microsoft.DotNet.SDK.8
```

### 2.3 Network access

The standard Docker build downloads from `mcr.microsoft.com` (base images), Docker Hub (`mongo:7.0`, `node:20-alpine`, `nginxinc/nginx-unprivileged`), `api.nuget.org` (NuGet packages) and `registry.npmjs.org` (UI packages). Behind a corporate proxy that inspects TLS, use the prebuilt path (§4.6): it needs neither NuGet nor npm inside Docker, and no npm at all.

### 2.4 Verify the prerequisites

```powershell
git --version              # git version 2.x
docker version             # Client and Server sections both shown (Docker Desktop running)
docker compose version     # Docker Compose version v2.x
dotnet --list-sdks         # 8.0.xxx
```

### 2.5 Get the source code

**1. Clone the repository** (SSH):

```powershell
git clone git@github.com:orontamir/Ta9_OronTamir_HomeAssignment.git
```

If this fails with `Permission denied (publickey)`, the machine has no SSH key registered with GitHub. Clone over HTTPS instead. Git Credential Manager signs you in through the browser if needed (see §6.9):

```powershell
git config --global credential.helper manager
git clone https://github.com/orontamir/Ta9_OronTamir_HomeAssignment.git
```

**2. Switch to the `dev` branch**, which holds the submitted version:

```powershell
cd Ta9_OronTamir_HomeAssignment
git checkout dev
git log --oneline -3          # check: the latest commits are listed
```

All commands in this guide run from this folder (where `docker-compose.yml` is).

## 3. Deploy (local .NET)

Runs the three services directly on the machine with `dotnet run`. This is useful for development and debugging. Each service listens on its port from `Properties/launchSettings.json` and runs in the `Development` environment, with readable console logs and Swagger opening in the browser.

### 3.1 Build and test

```powershell
dotnet build Ta9.sln
dotnet test Ta9.sln                                   # ~400 tests; Docker must be running (MongoDB tests)
dotnet test Ta9.sln --filter "Category!=Integration"  # without Docker
```

### 3.2 Option A — with MongoDB (same storage as production)

Start MongoDB in a container, then each service in its own terminal:

```powershell
docker run -d --name ta9-mongo -p 27017:27017 mongo:7.0

dotnet run --project src/Ta9.JobService          # terminal 1 → http://localhost:5001
dotnet run --project src/Ta9.ProcessorService    # terminal 2 → http://localhost:5002
dotnet run --project src/Ta9.AuditService        # terminal 3 → http://localhost:5003
```

### 3.3 Option B — no database (in-memory storage)

Job Service and Audit Service can keep everything in memory. Nothing is persisted across restarts. Without a database, also turn off log storage in MongoDB (logs still go to the console). Otherwise every service prints a `[ta9-log-sink] could not store …` line to stderr every 30 s.

```powershell
# PowerShell (each line in its own terminal)
$env:Logging__Mongo__Enabled = "false"; $env:Storage__Provider = "InMemory"; dotnet run --project src/Ta9.JobService
$env:Logging__Mongo__Enabled = "false"; $env:Storage__Provider = "InMemory"; dotnet run --project src/Ta9.AuditService
$env:Logging__Mongo__Enabled = "false"; dotnet run --project src/Ta9.ProcessorService
```

```bash
# bash
Logging__Mongo__Enabled=false Storage__Provider=InMemory dotnet run --project src/Ta9.JobService
Logging__Mongo__Enabled=false Storage__Provider=InMemory dotnet run --project src/Ta9.AuditService
Logging__Mongo__Enabled=false dotnet run --project src/Ta9.ProcessorService
```

### 3.4 Verify

```powershell
curl.exe http://localhost:5001/health/ready     # Healthy
curl.exe http://localhost:5002/health/ready     # Healthy (Degraded until the WebSocket to the Job Service is up)
curl.exe http://localhost:5003/health/ready     # Healthy
powershell -ExecutionPolicy Bypass -File .\scripts\smoke-test.ps1
```

### 3.5 Web UI (optional, development server)

With the services running (§3.2 or §3.3), from the `ui` folder:

```powershell
npm ci              # once
npm start           # http://localhost:4200, proxies /api/jobs → :5001 and /api/audit → :5003 (proxy.conf.json)
npm run test:ci     # 21 component tests, headless Chrome
```

On Windows, if `npm` opens a "Select an app" dialog, see §6.8.

### 3.6 Configuration reference

All settings are in each service's `appsettings.json` and can be overridden with environment variables. Use `__` (double underscore) for nesting.

| Service | Environment variable | Default | Meaning |
|---|---|---|---|
| Job | `Storage__Provider` | `Mongo` | `Mongo` or `InMemory` |
| Job | `Storage__Mongo__ConnectionString` | `mongodb://localhost:27017` | MongoDB connection |
| Job | `Storage__Mongo__Database` | `ta9_jobs` | Database name |
| Job | `Processor__BaseUrl` | `http://localhost:5002/` | Processor REST endpoint |
| Job | `Audit__BaseUrl` | `http://localhost:5003/` | Audit REST endpoint |
| Job | `Dispatch__MaxDispatchAttempts` | `5` | Failed dispatch attempts before a job is Failed |
| Job | `Reconcile__StaleAfter` | `00:01:00` | Silence before the reconciler checks a job |
| Processor | `JobServiceEvents__Url` | `ws://localhost:5001/ws/processor-events` | Job Service WebSocket |
| Processor | `Processing__MaxConcurrentJobs` | `10` | Jobs processed at the same time |
| Processor | `Processing__QueueCapacity` | `100` | Waiting jobs before 503 |
| Processor | `Processing__DocParallelismPerJob` | `2` | Documents analyzed in parallel per job |
| Audit | `Storage__Provider` | `Mongo` | `Mongo` or `InMemory` |
| Audit | `Storage__ConnectionString` | `mongodb://localhost:27017` | MongoDB connection |
| All | `Logging__UseJsonConsole` | `true` (`false` in Development) | JSON or human-readable console logs |
| All | `Logging__Console__MinimumLevel` | `Information` | Lowest level printed to the console (`docker compose logs`) |
| All | `Logging__Mongo__Enabled` | `true` | Store logs in MongoDB |
| All | `Logging__Mongo__ConnectionString` | `mongodb://localhost:27017` | Log database server (`mongodb://mongo:27017` in Docker) |
| All | `Logging__Mongo__Database` / `__Collection` | `ta9_logs` / `logs` | Where logs are stored (one collection for all services) |
| All | `Logging__Mongo__MinimumLevel` | `Debug` | Lowest level stored in MongoDB |
| All | `Logging__Mongo__RetentionDays` | `7` | TTL: older records are deleted by MongoDB automatically |
| All | `Serilog__MinimumLevel__Default` | `Debug` | Global gate: nothing below it is logged anywhere |

## 4. Deploy to Docker Desktop

`docker-compose.yml` builds the three service images and starts them together with MongoDB. It adds health checks and starts the containers in order: MongoDB → Job + Audit → Processor.

![Deployment](../hld/images/deployment.png)

### 4.1 Prepare Docker Desktop

1. Start **Docker Desktop** and wait until it reports **Engine running**.
2. *Settings → Resources:* give it at least **4 GB RAM** and **2 CPUs**.
3. *(Windows)* *Settings → General:* **Use the WSL 2 based engine** is enabled.
4. Make sure ports **5001–5003** and **27017** are free. Stop a local MongoDB if one is running.

### 4.2 Build and start

From the repository root:

```powershell
docker compose up --build -d
```

The first build downloads the .NET SDK and runtime images, the NuGet packages, and for the Web UI the Node image and the npm packages, which takes a few minutes. Later builds reuse the cached restore / install layers.

### 4.3 Verify

```powershell
docker compose ps
```

All five containers must show **(healthy)**:

```text
NAME                      IMAGE                   STATUS                    PORTS
ta9-audit-service-1       ta9/audit-service       Up 18 seconds (healthy)   0.0.0.0:5003->8080/tcp
ta9-job-service-1         ta9/job-service         Up 17 seconds (healthy)   0.0.0.0:5001->8080/tcp
ta9-mongo-1               mongo:7.0               Up 24 seconds (healthy)   0.0.0.0:27017->27017/tcp
ta9-processor-service-1   ta9/processor-service   Up 12 seconds (healthy)   0.0.0.0:5002->8080/tcp
ta9-ui-1                  ta9/ui                  Up 10 seconds (healthy)   0.0.0.0:4200->8080/tcp
```

The system is ready. **How to use it (Web UI, Swagger, Postman) is described in the User Guide.** For a quick check, open the **Web UI** at **http://localhost:4200**: *New job* → *Generate sample* (e.g. 100 documents) → *Create job*. The details page follows the job live ("● live") until *Completed*, then shows the results table and, a few seconds later, the audit entry.

Then run the smoke test, which creates a job, waits for it to finish, and prints the results and the audit entry:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\smoke-test.ps1
```

Expected output (abridged):

```text
Created 1 job(s) with 3 document(s) each
01dba5dd-…  Completed  3 docs  succeeded=3 failed=0 words=54
All jobs finished in 1.1s
  [0] doc-0.txt: words=18 unique=13 top=[the(3), 0(2), fox(2), quick(2), brown(1)] sha256=94b29bd5d7ae...
Audit: Completed, recorded at 2026-10-26T14:57:53.94+00:00
```

The assignment's load scenario, 10 concurrent jobs × 1,000 documents:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\smoke-test.ps1 -Jobs 10 -Documents 1000
```

Other ways to try the API:
- **Swagger UI:** http://localhost:5001/swagger
- **Postman:** import `docs/postman/Ta9.postman_collection.json` and run the requests in order. "Create job" stores `{{jobId}}` for the rest.
- **Live status stream (SSE):** `curl.exe -N http://localhost:5001/api/jobs/<job-id>/events`

### 4.4 Operate

| Task | Command |
|---|---|
| Follow logs of one service | `docker compose logs -f job-service` |
| Follow all logs | `docker compose logs -f` |
| Restart a service (e.g. to see the WebSocket reconnect) | `docker compose restart job-service` |
| Rebuild after a code change | `docker compose up --build -d` |
| Change a setting | edit `environment:` in `docker-compose.yml`, then `docker compose up -d` |
| Stop (keep data) | `docker compose down` |
| Stop and delete MongoDB data | `docker compose down -v` |
| Inspect MongoDB | `docker compose exec mongo mongosh ta9_jobs --eval "db.jobs.find().limit(5)"` |
| Logs of one job (all services, Debug and up) | `docker compose exec mongo mongosh ta9_logs --quiet --eval "db.logs.find({jobId:'<job-id>'},{_id:0,timestamp:1,service:1,level:1,message:1}).sort({timestamp:1})"` |
| Latest errors | `docker compose exec mongo mongosh ta9_logs --quiet --eval "db.logs.find({level:'Error'}).sort({timestamp:-1}).limit(20)"` |

The containers also appear in the Docker Desktop dashboard under **Containers → ta9**, where logs, health and resource usage are visible.

### 4.5 Resilience demo (optional)

```powershell
docker compose restart job-service
docker compose logs -f processor-service
```

While the Job Service restarts, the processor logs `Job Service events connection failed (attempt N)`, reconnects with exponential backoff, and replays every event that was not acknowledged. No job is lost.

### 4.6 Restricted networks — prebuilt images

If the build fails with `NU1301` (see §6.1), or the UI build cannot download npm packages (§6.8), publish the services on the host, where restore works, and let Docker only package the binaries. The Web UI is not built at all on this path: Docker packages its committed production build, `ui/prebuilt`, so **no npm is needed**.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\docker-up-prebuilt.ps1
```

The script is equivalent to:

```powershell
foreach ($p in "Ta9.JobService","Ta9.ProcessorService","Ta9.AuditService") {
    dotnet publish "src/$p/$p.csproj" -c Release -o "artifacts/$p" /p:UseAppHost=false
}
docker compose -f docker-compose.yml -f docker-compose.prebuilt.yml up --build -d
```

It uses `Dockerfile.prebuilt` (services) and `ui/Dockerfile.prebuilt` (packages `ui/prebuilt`). After changing the UI, refresh `ui/prebuilt` on a machine where npm works: `cd ui; npm ci; npm run build:prebuilt`. The compose file, health checks and configuration are identical to the standard path. Verify as in §4.3.

## 5. Troubleshooting — where to look

When the system **does not start**, **gets stuck** or **crashes**, the answer is almost always in one of four places. Check them in this order.

### 5.1 The four places

| # | Where | What it shows | Command |
|---|---|---|---|
| 1 | **Container state** | Which service is down, restarting or unhealthy | `docker compose ps` (Docker Desktop: *Containers → ta9*) |
| 2 | **Console logs** (`docker compose logs`) | The **flow** (Information), warnings and errors of each service. Written synchronously, so the **last lines before a crash are always here** | `docker compose logs --tail 200 <service>` · `docker compose logs -f` |
| 3 | **MongoDB `ta9_logs.logs`** | **Everything from Debug up**, from all three services, for 7 days, searchable by job, correlation id, level and service | `docker compose exec mongo mongosh ta9_logs` (queries in §5.3) |
| 4 | **Health endpoints** | Whether each service and its dependencies are usable now | `curl.exe http://localhost:5001/health/ready` (and :5002, :5003) |

Every log record carries `Service`, `CorrelationId` and, inside job processing, `JobId`. The **correlation id** follows one job from the API call through dispatch, processing, WebSocket events and audit. To see the whole story of a job across the three services, query by `jobId` or `correlationId`.

### 5.2 Log levels

| Level | Meaning | Examples | Console | MongoDB |
|---|---|---|---|---|
| **Debug** | Detail for investigating a failure | `Claimed job … for dispatch`, `Progress 300/1000`, `Acknowledged JobCompleted …`, `POST api/process … returned 202` | — | ✔ |
| **Information** | The normal **flow** | `Job … created`, `Job … dispatched to the processor`, `Job started with 1000 documents`, `Job completed …`, `Applied JobCompleted …`, `Recorded audit entry …`, `HTTP POST /api/jobs responded 201` | ✔ | ✔ |
| **Warning** | Something went wrong and the system handles it | `Processor unreachable (attempt 2) … Next try at …`, `Document 7 (a.txt) failed`, `Job Service events connection failed (attempt 3)` | ✔ | ✔ |
| **Error** | An operation failed; usually needs attention | `Giving up on job …`, `Processor rejected job …`, `Job failed: all 3 documents failed`, `Request failed: Storage unavailable`, `Dispatch loop iteration failed` (+ stack trace) | ✔ | ✔ |
| **Critical** | The process is about to terminate | `Unhandled exception (process terminating: True)` (+ stack trace) | ✔ | ✔ (flushed before exit) |

To see Debug on the console too, without rebuilding: set `Logging__Console__MinimumLevel: Debug` under `environment:` in `docker-compose.yml`, then run `docker compose up -d`.

### 5.3 Useful log queries (mongosh)

Open a shell with `docker compose exec mongo mongosh ta9_logs`, then:

```javascript
// The full story of one job, all services, oldest first
db.logs.find({ jobId: "<job-id>" }, { _id: 0, timestamp: 1, service: 1, level: 1, message: 1 }).sort({ timestamp: 1 })

// Errors and crashes in the last hour, with stack traces
db.logs.find({ level: { $in: ["Error", "Critical"] }, timestamp: { $gt: new Date(Date.now() - 3600 * 1000) } }).sort({ timestamp: -1 })

// One request / flow by correlation id (X-Correlation-Id response header)
db.logs.find({ correlationId: "<id>" }).sort({ timestamp: 1 })

// Latest lines of one service, e.g. after a restart
db.logs.find({ service: "processor-service" }).sort({ timestamp: -1 }).limit(50)

// Startup lines of each service: where it listens and how it logs
db.logs.find({ message: /^(Service started|Logging:)/ }).sort({ timestamp: -1 }).limit(6)
```

### 5.4 The system does not start

1. `docker compose ps`. A service that is `Exited`, `Restarting` or stays `(health: starting)` is the one to inspect.
2. `docker compose logs --tail 100 <service>`. A healthy start always prints two lines:
   - `Logging: console >= Information; MongoDB ta9_logs.logs >= Debug (retention 7 days)`
   - `Service started: environment Production, version …, listening on http://[::]:8080`

   If the second line is missing, the reason is just above it.

| Symptom | Where | Typical cause | Fix |
|---|---|---|---|
| `mongo` unhealthy; the other services never start | `docker compose logs mongo` | Port 27017 taken, corrupt volume, not enough disk | Free the port; `docker compose down -v` (deletes data); free disk space |
| `Hosting failed to start` / `OptionsValidationException` | Service console log | Invalid setting, e.g. empty `Storage__Mongo__ConnectionString` | Fix the variable in `docker-compose.yml`; `docker compose up -d` |
| `Bind for 0.0.0.0:5001 failed: port is already allocated` | `docker compose up` output | Another process uses the port | Stop it or change the host port (`"5101:8080"`) |
| Build fails with `NU1301` | `docker compose up --build` output | TLS-inspecting proxy | §4.6 prebuilt images |
| Processor `/health/ready` = `Degraded` | Processor log: `Job Service events connection failed (attempt N)` | Job Service not up yet / wrong `JobServiceEvents__Url` | Wait (it reconnects); check the URL |
| `[ta9-log-sink] … could not store N log events` on stderr | Service console log | Log database unreachable; **the service itself still works** | Check `mongo`; `Logging__Mongo__ConnectionString` |
| `ui` build fails (`npm ci` / `ng: not found` / SSL handshake) | `docker compose up --build` output | npm registry not reachable from Docker on this network | §6.8; use §4.6 (prebuilt, no npm) |

### 5.5 The system is stuck (jobs do not move)

Find where the job stopped. `GET /api/jobs/<id>` shows the status. The job's log records (§5.3) show the last step that happened.

| Job stuck in | Look for (MongoDB or console) | Meaning | Action |
|---|---|---|---|
| `Pending` | Job Service: `Processor unreachable (attempt N) … Next try at …` / `Processor busy; job … will be dispatched again at …` | Dispatcher cannot hand the job over; retries with backoff (after 5 failed attempts the job becomes **Failed**: `Giving up on job …`) | `docker compose ps processor-service`; processor logs; `curl.exe :5002/health/ready` |
| `Pending`, no dispatcher lines at all | Job Service: `Dispatch loop iteration failed` (Error) or `Job dispatching is disabled by configuration` | Storage error in the loop, or `Dispatch__Enabled=false` | Fix MongoDB / the setting |
| `Queued` | Processor: `Job … accepted`, then `Worker N picked up job …` (Debug) | Waiting for a free worker (10 jobs run at once, 100 can wait) | Normal under load; see `Processing__MaxConcurrentJobs` |
| `Processing`, progress not changing | Processor: `Progress x/y` (Debug); Job Service: `Applied JobProgress…` | Large job still running, or events not reaching the Job Service | Processor `/health/ready` = `Degraded` → WebSocket down; it reconnects and replays |
| `Processing` after a processor restart | Job Service: `Processor no longer knows job …; redispatching` / `Reconciliation: …` | Reconciler recovers the job within ~1–2 minutes (`Reconcile__StaleAfter`) | Wait; after 3 redispatches the job is Failed |
| Finished, but no audit entry | Job Service: `Audit service unavailable; retrying in …` | Audit Service down; summaries are kept and sent later | `docker compose ps audit-service`; audit logs |
| API calls return `503 Storage unavailable` | Job Service: `Request failed: Storage unavailable` (Error) | MongoDB unreachable | `docker compose ps mongo`; `docker compose logs mongo` |
| Web UI shows "The service cannot be reached" or `502 Bad Gateway` | `docker compose logs ui` (nginx) and `docker compose ps job-service audit-service` | nginx cannot reach a service (not started, unhealthy, restarting) | Start / fix the service; the UI recovers on the next refresh |
| Web UI details page never shows "● live" / progress does not move | Browser developer tools → Network → `events` (EventSource) | SSE blocked by a proxy in front of the UI, or the job is already finished | The bundled nginx streams SSE (`proxy_buffering off`); an extra proxy must do the same. The page still updates on reload |

### 5.6 A service crashes or keeps restarting

1. `docker compose ps` shows `Restarting`, or a recent `Up` time. Get the details with `docker inspect --format "{{.State.ExitCode}} {{.State.OOMKilled}} {{.RestartCount}}" ta9-processor-service-1`.
2. `docker compose logs --tail 200 <service>`. Look for **`Unhandled exception (process terminating: True)`** (Critical, with stack trace) or `Error` lines just before the stop.
3. MongoDB: `db.logs.find({ service: "<service>", level: { $in: ["Error", "Critical"] } }).sort({ timestamp: -1 }).limit(20)`.

| Finding | Meaning | Action |
|---|---|---|
| `Critical … Unhandled exception` with a stack trace | A bug escaped every handler | Report the stack trace and correlation id |
| Exit code `137` and `OOMKilled true` | Container killed for memory | Raise the Docker Desktop memory limit or lower `Processing__MaxConcurrentJobs` |
| Exit code `137`, no OOM, nothing in the logs | Killed from outside (`docker kill`, Docker Desktop restart) | — |
| `Worker N crashed on job …` (Error) | One job failed unexpectedly. **The worker and the service survive** | Inspect the stack trace; the job becomes Failed |

Records still in the buffer (up to ~1 s of Debug detail) can be missing from MongoDB after a hard kill (`137`). For a crash, the **console log is the source of truth**. On an unhandled exception the buffer is flushed before the process exits.

### 5.7 Logs are missing in MongoDB

| Check | How |
|---|---|
| Logging to MongoDB is on | First lines of the service: `Logging: … MongoDB ta9_logs.logs >= Debug` (not `disabled`) |
| The log database is reachable | No `[ta9-log-sink] … could not store` lines on the console |
| Nothing was dropped | `db.logs.find({ message: /log events were not stored/ })`. After an outage or overload, the first stored batch reports how many records were lost |
| The level is stored | `Logging__Mongo__MinimumLevel` and `Serilog__MinimumLevel__Default` must both allow it |
| Records are not too old | Records older than `Logging__Mongo__RetentionDays` (7) are deleted by the TTL index |

## 6. Error Messages Encountered & Fixes

The errors below were encountered while building and deploying this system, with their root cause and the fix applied.

### 6.1 Docker build fails: NU1301 Unable to load the service index

**Symptom:** `docker compose up --build` stops at the restore step:

```text
error NU1301: Unable to load the service index for source https://api.nuget.org/v3/index.json.
[/src/src/Ta9.AuditService/Ta9.AuditService.csproj]
target audit-service: failed to solve: process "/bin/sh -c dotnet restore src/${PROJECT}/${PROJECT}.csproj"
did not complete successfully: exit code: 1
```

**Diagnosis:** HTTPS from inside a container fails, while the same restore works on the host:

```powershell
docker run --rm mcr.microsoft.com/dotnet/sdk:8.0 curl -sSI https://api.nuget.org/v3/index.json
```

```text
curl: (60) SSL certificate problem: self-signed certificate in certificate chain
```

**Cause:** A corporate proxy performs **TLS inspection** and re-signs HTTPS traffic with an internal certificate. The host trusts that certificate; the container does not. (Plain HTTP, e.g. the `apt-get` step, still works.)

**Fix:** Use the prebuilt path (§4.6). Do **not** bake a corporate CA certificate into the repository or the images. If your IT department allows it, an alternative is to add the corporate root CA to Docker Desktop and the build, but that is environment-specific and not part of this repository.

### 6.2 PowerShell: script is not digitally signed

**Symptom:**

```text
.\scripts\docker-up-prebuilt.ps1 : File \\server\share\TA9\scripts\docker-up-prebuilt.ps1 cannot be loaded.
The file ... is not digitally signed. You cannot run this script on the current system.
    + CategoryInfo          : SecurityError: (:) [], PSSecurityException
    + FullyQualifiedErrorId : UnauthorizedAccess
```

**Cause:** The PowerShell execution policy treats scripts on a network share, or downloaded from the internet, as remote and requires a signature.

**Fix** (affects only this one run):

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\docker-up-prebuilt.ps1
```

For a repository downloaded as a ZIP, `Get-ChildItem -Recurse .\scripts | Unblock-File` also works. If a group policy blocks `Bypass`, run the commands listed in §4.6 manually.

### 6.3 git push: 403, permission denied to another account

**Symptom:**

```text
remote: Permission to orontamir/Ta9_OronTamir_HomeAssignment.git denied to <other-account>.
fatal: unable to access 'https://github.com/orontamir/Ta9_OronTamir_HomeAssignment.git/': The requested URL returned error: 403
```

**Cause:** Git Credential Manager (or a `GIT_ASKPASS` helper) supplies cached credentials of a different GitHub account, e.g. a work account, for every `github.com` URL.

**Fix:** Put the intended user name in the remote URL so the credential manager asks for that account. Sign in with a browser session or a Personal Access Token (*Contents: Read and write*) of that account:

```powershell
git remote set-url origin https://orontamir@github.com/orontamir/Ta9_OronTamir_HomeAssignment.git
git push -u origin main
```

Do not delete the other account's stored credential, because other repositories still need it.

### 6.4 git push: GH007, push would publish a private email address

**Symptom:**

```text
remote: error: GH007: Your push would publish a private email address.
 ! [remote rejected] main -> main (push declined due to email privacy restrictions)
```

**Cause:** The commits are authored with an email that GitHub keeps private (*Settings → Emails → Block command line pushes that expose my email*).

**Fix:** Author the commits with the GitHub **noreply** address from *Settings → Emails* and rewrite the unpushed commits:

```powershell
git config user.email "<id>+<user>@users.noreply.github.com"
git rebase --root --exec "git commit --amend --no-edit --reset-author"
git push -u origin main
```

Environment variables such as `GIT_AUTHOR_EMAIL` override `git config`. If they are set, as in some managed workstations, set them for the commit as well.

### 6.5 git commit hangs on a network share

**Symptom:** `git commit` never finishes. The process waits on I/O while reading `.git/objects/pack/*.pack`.

**Cause:** The working copy sits on a network share accessed by two machines at once (NFS and SMB), and another git client, such as an IDE's git integration, holds files in `.git` open.

**Fix:** Close IDEs and git tools on the other machine while committing; the commit then completes. Better still, clone the repository to a local disk.

### 6.6 Issues caught by the automated tests during development (already fixed in the code)

| Failing tests / symptom | Cause | Fix |
|---|---|---|
| 11 MongoDB tests failed: queries by id or jobId returned nothing, while unfiltered reads worked | MongoDB.Driver 2.x legacy GUID mode encoded stored values and query values differently | GUIDs stored as strings with explicit per-member serializers (decision D22) |
| `404` returned `application/json` instead of `application/problem+json` | `[Produces("application/json")]` on the controller overrode ProblemDetails responses | Attribute removed (RFC 7807 compliance) |
| Empty document content (`""`) rejected with 400; whole jobs would have failed in the processor | MVC adds an implicit `[Required]` to non-nullable strings | Implicit rule disabled; validators are the single source of input rules (D23) |
| Runner tests saw unexpected `JobProgress` events | Small jobs legitimately report progress per document | Lifecycle assertions ignore progress; progress is tested separately |
| Smoke test printed `Created  job(s)` (count missing) | Windows PowerShell 5.1 has no `.Count` on a single object | Result wrapped in `@()` |
| No `HTTP … responded …` request lines in the logs | Serilog's request logging used the static `Log.Logger`, which the services never configure | The middleware now uses the service's own logger |
| Error lines of failed requests had no correlation id | Exception handling ran before the correlation middleware | Correlation middleware runs first |

### 6.7 Other messages you may see

| Message | Meaning / fix |
|---|---|
| `Job Service events connection failed (attempt N)` in processor logs | **Expected** while the Job Service is starting or restarting; the processor reconnects automatically. Persistent: check `JobServiceEvents__Url` |
| `/health/ready` of the processor returns `Degraded` | The WebSocket link is down; work is still accepted and events are buffered. Resolves when the Job Service is reachable |
| API returns `503 Storage unavailable` | MongoDB is unreachable: `docker compose ps mongo`, `docker compose logs mongo` |
| `[ta9-log-sink] … could not store N log events: TimeoutException …` on stderr | The log database is unreachable. The service keeps working and logs still go to the console (§5.7) |
| Warning `N log events were not stored (buffer full or log database unavailable)` in `ta9_logs.logs` | Records lost during an outage or an extreme burst. The count is exact; the console has the full sequence |
| `Bind for 0.0.0.0:5001 failed: port is already allocated` | Another process uses the port: stop it, or change the host port in `docker-compose.yml` (e.g. `"5101:8080"`) |
| `error during connect … docker_engine` / `Cannot connect to the Docker daemon` | Docker Desktop is not running: start it and wait for *Engine running* |
| Integration tests fail with `Docker is either not running or misconfigured` | Testcontainers needs Docker Desktop running, or run `dotnet test --filter "Category!=Integration"` |
| `fatal: detected dubious ownership in repository` | Repository on a network share: `git config --global --add safe.directory '<path>'` |

### 6.8 Web UI: npm and build problems

| Message | Cause | Fix |
|---|---|---|
| Docker build: `npx ng build` → `request to https://registry.npmjs.org/ng failed … SSL alert handshake failure` | `npx` looked for a package called `ng` instead of using the local Angular CLI, and the registry is blocked | Fixed in the Dockerfile: the local CLI is called directly (`node node_modules/@angular/cli/bin/ng.js build`) |
| Docker build: `sh: ng: not found` / *"npm install incomplete"* | `npm ci` could not download every package (TLS inspection) | Use the prebuilt path (§4.6), which needs no npm |
| `npm ci` on the host: `ERR_SSL_SSL/TLS_ALERT_HANDSHAKE_FAILURE … websocket-driver-0.7.5.tgz` (followed by `EPERM … Failed to remove some directories`) | The network blocks some npm downloads; the `EPERM` lines are only npm's cleanup after the failure | Use the prebuilt path (§4.6); or ask IT for the organization's npm registry and set it on your machine only (`npm config set registry <address>`) |
| Typing `npm` opens a Windows **"Select an app to open 'npm'"** dialog | PowerShell found the extension-less `npm` shell script instead of `npm.cmd` / `npm.ps1` (scripts blocked, or `.CMD` missing from `PATHEXT`) | Use `npm.cmd`, or `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned` and a new PowerShell; check that `$env:PATHEXT` contains `.CMD`. The helper script already uses `npm.cmd` |
| `npm -v` prints nothing | npm is missing from the Node installation | Reinstall Node.js LTS from nodejs.org with *npm package manager* selected |
| `The Angular CLI requires…` / unsupported Node warning | Angular 17 officially lists Node 18 / 20 | Node 24 works (tested); the warning can be ignored |

### 6.9 git clone: Permission denied (publickey)

```text
git@github.com: Permission denied (publickey).
fatal: Could not read from remote repository.
```

**Cause:** an SSH address (`git@github.com:…`) needs an SSH key that is registered with your GitHub account, and this machine has none. A related message is `Permission to … denied to <other-account>`: SSH used another account's key.

**Fix:** clone over HTTPS with Git Credential Manager. It signs you in through the browser; make sure the browser is signed in to the right GitHub account before clicking *Authorize*.

```powershell
git config --global credential.helper manager
git clone https://github.com/orontamir/Ta9_OronTamir_HomeAssignment.git
cd Ta9_OronTamir_HomeAssignment
git checkout dev
```

To keep using SSH instead: `ssh-keygen -t ed25519`, add the `.pub` file under GitHub → *Settings → SSH and GPG keys*, then check with `ssh -T git@github.com`.

## 7. Uninstall

```powershell
docker compose down -v                      # remove containers, network and MongoDB volume
docker image rm ta9/job-service ta9/processor-service ta9/audit-service ta9/ui
docker rm -f ta9-mongo                      # only if started manually in §3.2
```
