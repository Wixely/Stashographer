# Stashographer

A household inventory manager. Scan a barcode, ISBN, or QR code (or type it in) and
Stashographer auto-fills the details from free public APIs — or, optionally, an AI vision
model — then tracks quantity, where each thing lives (room → container), expiry dates, and
who currently has it on loan.

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

## Tech

.NET 10 Blazor Web App (Interactive Server) · SQLite via **Dapper** with hand-written SQL
migrations · MudBlazor · QRCoder · Microsoft.Extensions.AI. MIT-licensed, permissive
dependencies only (see [THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES.md)).

## Run locally

```bash
dotnet run --project src/Stashographer
```

Migrations apply automatically on startup and the SQLite file (`stashographer.db`) is created
next to the app. Open the printed URL.

## Enable AI (optional)

Set the `Ai` configuration (env vars shown; any OpenAI-compatible endpoint works):

```bash
Ai__Enabled=true
Ai__ApiKey=sk-...
Ai__Model=gpt-4o-mini
Ai__Endpoint=https://your-endpoint/v1   # optional (Azure OpenAI, Ollama, LM Studio, …)
```

Without it, AI actions are simply hidden and everything else works.

## Docker

```bash
docker build -t stashographer .
docker run -p 8080:8080 -v stash-data:/data stashographer
```

The database is stored on the `/data` volume so it survives restarts.

## Tests

```bash
dotnet test
```

## Roadmap (phase 2)

Postgres/other database providers, tags UI, shopping lists from low stock, consumption
history, CSV import/export, optional multi-user auth, PWA/offline.
