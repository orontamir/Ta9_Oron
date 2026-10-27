# TA9 UI (Angular 17)

A small web console over the REST APIs: list jobs (filter, paging, auto-refresh), create a job (type text,
load `.txt` files or generate up to 1,000 sample documents), follow a job live (Server-Sent Events), cancel or
delete it, page through the per-document results, and see the audit entry.

| | |
|---|---|
| Stack | Angular 17.3 (standalone components, signals, `@if` / `@for`), plain CSS, no UI library |
| API access | Relative URLs `/api/jobs`, `/api/audit`. nginx (Docker) or the dev-server proxy forwards them to the services, so there is one origin and no CORS. The services are unchanged |

## Run

| How | Command | URL |
|---|---|---|
| With the whole stack (Docker) | `docker compose up --build -d` (repository root) | http://localhost:4200 |
| Development server (services running on 5001 / 5003) | `npm ci` then `npm start` | http://localhost:4200 |

The Docker build needs nothing on the host (tested with Node 20 in the image; local builds work with Node 20–24).

**Restricted networks.** If npm cannot download packages (TLS inspection), `scripts/docker-up-prebuilt.ps1` packages
the committed build output in `prebuilt/` instead: no npm needed. After changing the UI, refresh it on a machine where
npm works: `npm ci && npm run build:prebuilt`.

## Test

```bash
npm ci
npm test             # Karma + Jasmine in Chrome (watch mode)
npm run test:ci      # single run, headless Chrome
```

## Structure

| Path | Content |
|---|---|
| `src/app/api` | `JobsApi` (REST + SSE), contract types, ProblemDetails → message |
| `src/app/jobs-list` | `GET /api/jobs` with status filter, paging, auto-refresh |
| `src/app/job-create` | `POST /api/jobs`; client-side hints for the server's limits |
| `src/app/job-detail` | Live status (SSE), cancel / delete, results, audit entry |
| `nginx.conf` | Static files + reverse proxy (SSE without buffering) |
