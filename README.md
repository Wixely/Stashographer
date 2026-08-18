# Stashographer

[![CI](https://github.com/Wixely/Stashographer/actions/workflows/ci.yml/badge.svg)](https://github.com/Wixely/Stashographer/actions/workflows/ci.yml)
[![Docker](https://github.com/Wixely/Stashographer/actions/workflows/docker.yml/badge.svg)](https://github.com/Wixely/Stashographer/actions/workflows/docker.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/)

A household inventory manager. Scan a barcode, ISBN, or QR code (or type it in) and
Stashographer auto-fills the details from free public APIs — or, optionally, an AI vision
model — then tracks quantity, where each thing lives (room → container), expiry dates, and
who currently has it on loan.

Self-hosted, single container, SQLite on a volume. No account, no cloud, no telemetry.

## Screenshots

| Dashboard | Inventory |
|---|---|
| ![Dashboard — item counts, expiring soon, low stock, currently out](docs/screenshots/dashboard.png) | ![Inventory — filterable item list with inline quantity controls](docs/screenshots/inventory.png) |

| Scan & add | Places |
|---|---|
| ![Scan & add — camera scanning, manual barcode entry, add from photo](docs/screenshots/scan.png) | ![Places — rooms and containers explorer](docs/screenshots/places.png) |

Every container gets a printable QR code — scan it to see what's meant to be inside:

![Container view — contents of a shelf with its QR code](docs/screenshots/container.png)

## Features

- **Any kind of item** — groceries, books, tools, electronics, clothing, or anything else,
  with flexible per-item attributes.
- **Barcode & ISBN lookup** — [Open Food Facts](https://world.openfoodfacts.org) for
  groceries and [Open Library](https://openlibrary.org) for books, routed automatically by
  the code's shape.
- **Camera scanning** via the browser-native `BarcodeDetector`, with manual entry always
  available (also works with USB keyboard-wedge scanners).
- **Locations & containers** — put items in a room or inside a box/shelf/drawer/bin. Each
  container gets a **printable QR code**; scan it to see what's (meant to be) inside.
- **Checkout / lending** — record who took an item and where, and check it back in later.
- **Dashboard** — low stock, expiring soon, and currently checked-out at a glance.
- **AI enrichment (optional)** — identify an item from a photo when a barcode won't do, and
  "season" any item with extra detail, via any **OpenAI-protocol** endpoint.
- **Light / dark themes**, mobile-friendly (MudBlazor).

## Quick start

### Docker Compose (recommended)

```bash
docker compose up -d --build
```

Then open <http://localhost:8080>. A sample [`docker-compose.yml`](docker-compose.yml) is
included — the database and images live on the `stash-data` volume, so `docker compose down`
keeps your data.

### Docker

```bash
docker build -t stashographer .
docker run -d -p 8080:8080 -v stash-data:/data stashographer
```

The image runs unprivileged (UID 1654) and exposes a `/health` endpoint used by its
`HEALTHCHECK`. If you swap the named volume for a bind mount, `chown 1654` the host directory
first — bind mounts keep the host's ownership, so the app cannot write to it otherwise.

### From source

```bash
dotnet run --project src/Stashographer
```

Migrations apply automatically on startup and the SQLite file (`stashographer.db`) is created
next to the app. Open the printed URL.

## Enable AI (optional)

Configure it in **Settings → AI enrichment** — endpoint, API key and models are saved to the
app database and apply immediately (no restart; works inside Docker). Any OpenAI-compatible
endpoint works (OpenAI, Azure OpenAI, Ollama, LM Studio, …).

Environment variables provide the initial defaults until settings are saved in the UI:

```bash
Ai__Enabled=true
Ai__ApiKey=sk-...
Ai__Model=gpt-4o-mini
Ai__VisionModel=gpt-4o           # optional, for photo identify/detect/match
Ai__Endpoint=https://your-endpoint/v1   # optional
```

Without AI configured, those actions are simply hidden and everything else works.

> Keep the key out of git. Put it in a `.env` file next to `docker-compose.yml` (already
> covered by `.gitignore`) and reference it as `${STASH_AI_API_KEY}`.

## Tech

.NET 10 Blazor Web App (Interactive Server) · SQLite via **Dapper** with hand-written SQL
migrations · MudBlazor · QRCoder · Microsoft.Extensions.AI. MIT-licensed, permissive
dependencies only (see [THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES.md)).

## Development

```bash
dotnet test                    # 70 tests, no network access required
dotnet build -c Release
```

CI runs build + tests on every push and PR to `main`, and builds the Docker image; pushes to
`main` publish it to `ghcr.io/wixely/stashographer` and smoke-test that the container serves
`/health`.

Sample data is seeded automatically in the Development environment (see
`appsettings.Development.json`) — that is what the screenshots above show.

## Roadmap (phase 2)

Postgres/other database providers, tags UI, shopping lists from low stock, consumption
history, CSV import/export, optional multi-user auth, PWA/offline.

## License

MIT — see [LICENSE](LICENSE).
