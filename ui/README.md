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

Requires Node.js 18.13+ or 20.9+ for local builds; the Docker build needs nothing on the host.

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
