# Third-party notices

Stashographer is MIT-licensed and depends only on permissive (non-copyleft) components.

| Component | License | Use |
|-----------|---------|-----|
| MudBlazor | MIT | UI component library, theming (light/dark) |
| Dapper | Apache-2.0 | Micro-ORM for data access |
| Microsoft.Data.Sqlite | MIT | SQLite ADO.NET provider |
| QRCoder | MIT | Server-side QR code generation for container labels |
| Microsoft.Extensions.AI / .OpenAI | MIT | Optional AI enrichment over the OpenAI protocol |
| ASP.NET Core / Blazor (Microsoft.*) | MIT | Web framework |

Browser barcode scanning uses the native [`BarcodeDetector`](https://developer.mozilla.org/docs/Web/API/BarcodeDetector)
Web API where available (no third-party code). On browsers without it (e.g. iOS Safari),
scanning degrades to manual entry; any optional fallback decoder must be permissively
licensed (MIT/BSD/Apache-2.0) and vendored as a static asset — no npm/Node build step.

External data sources (no bundled code, used at runtime over HTTP):

- Open Food Facts — grocery barcode metadata (open data).
- Open Library — book metadata by ISBN (open data).
