# Documentation tools

Regenerate the diagram images and the Word documents from their sources.

| Source | Output | Command (from this folder) |
|---|---|---|
| `docs/hld/diagrams/ta9-hld.drawio` | one PNG per page in `docs/hld/images/rendered/` | `npm run render` |
| `docs/hld/HLD.md` | `docs/hld/TA9-HLD.docx` | `npm run hld` |
| `docs/installation/INSTALLATION.md` | `docs/installation/TA9-Installation-Guide.docx` | `npm run install-guide` |
| `docs/testing/STD.md` | `docs/testing/TA9-STD.docx` | `npm run std` |
| `docs/websocket/WEBSOCKET.md` | `docs/websocket/TA9-WebSocket-Behavior.docx` | `npm run websocket` |
| `docs/storage/STORAGE.md` | `docs/storage/TA9-Storage.docx` | `npm run storage` |
| `docs/logging/LOGGING.md` | `docs/logging/TA9-Logging-Observability.docx` | `npm run logging` |
| `docs/limitations/LIMITATIONS.md` | `docs/limitations/TA9-Limitations-Production.docx` | `npm run limitations` |
| `docs/ai-usage/AI-USAGE.md` | `docs/ai-usage/TA9-AI-Usage.docx` | `npm run ai-usage` |
| `docs/user-guide/USER-GUIDE.md` | `docs/user-guide/TA9-User-Guide.docx` | `npm run user-guide` |
| `docs/concurrency/CONCURRENCY.md` | `docs/concurrency/TA9-Concurrency-Performance.docx` | `npm run concurrency` |

Setup: Node.js 18+, then `npm install`. Rendering needs Google Chrome or Chromium; set `CHROME_PATH` if it is not in the default location.

After editing a diagram in draw.io you can also export it directly (*File → Export as → PNG*) to `docs/hld/images/` instead of using `npm run render`.
