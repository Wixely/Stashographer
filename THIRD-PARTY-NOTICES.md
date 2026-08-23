# Third-party notices

Stashographer is MIT-licensed and depends only on permissive (non-copyleft) components.

| Component | License | Use |
|-----------|---------|-----|
| MudBlazor | MIT | UI component library, theming (light/dark) |
| Dapper | Apache-2.0 | Micro-ORM for data access |
| Microsoft.Data.Sqlite | MIT | SQLite ADO.NET provider |
| SQLitePCLRaw.lib.e_sqlite3 | Public domain (SQLite) / Apache-2.0 wrapper | Native SQLite engine, pinned to latest |
| QRCoder | MIT | Server-side QR code generation for container labels |
| ZXing.Net | Apache-2.0 | Local server-side barcode decoding from fallback camera photos |
| SixLabors.ImageSharp (2.1.x) | Apache-2.0 | Image decoding and on-demand thumbnail generation (v2 pinned; v3+ is not permissively licensed) |
| Microsoft.Extensions.AI / .OpenAI | MIT | Optional AI enrichment over the OpenAI protocol |
| ASP.NET Core / Blazor (Microsoft.*) | MIT | Web framework |

Live browser barcode scanning uses the native [`BarcodeDetector`](https://developer.mozilla.org/docs/Web/API/BarcodeDetector)
Web API where available. When it is unavailable, ZXing.Net decodes a still camera image on
the Stashographer server; the image is neither persisted nor sent to an external service.

External data sources (no bundled code, used at runtime over HTTP):

- Open Food Facts — grocery barcode metadata (open data).
- Open Library — book metadata by ISBN (open data).
