# API and MCP automation

Stashographer exposes the same inventory and intake operations through a versioned HTTP API
and a stateless Streamable HTTP MCP server. Both surfaces are off by default. Automation may
inspect inventory and create or refine intake drafts, but it cannot accept or reject them:
final acceptance stays item-by-item in **Intake Queue**.

## Activate access

Access has two independent gates so restoring a database cannot unexpectedly expose an
endpoint on a new deployment.

1. Enable the deployment gates and restart Stashographer:

   ```text
   Stashographer__EnableApi=true
   Stashographer__EnableMcp=true
   ```

   For Docker Compose, set `STASHOGRAPHER_ENABLE_API=true` and
   `STASHOGRAPHER_ENABLE_MCP=true` in the untracked `.env` file. The checked-in local launch
   profiles make both controls available for development.
2. Sign in as the administrator and open **Settings → API & MCP automation**.
3. Generate an API key and copy it immediately. Only its hash and final eight characters are
   stored, so the full key cannot be shown again.
4. Enable API and save. Generate an MCP key as well when MCP must require authentication,
   then enable MCP and save.

MCP depends on an active API key and the API activation gate because its tools share the same
authoritative operations. An MCP key is optional by design for an explicitly trusted network;
without one, anyone who can reach `/mcp` can invoke every MCP tool. API always requires its
own key. Generating a replacement key immediately revokes the previous one.

Send keys as a bearer token:

```text
Authorization: Bearer stashographer_api_...
```

Unavailable deployment or application gates return `404`. A missing or invalid required key
returns `401` with `WWW-Authenticate: Bearer`.

## HTTP API v1

The API base is `/api/v1`. JSON enums are represented by names such as `Manual` and
`ReadyForReview`.

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/v1` | Identity, version and review policy |
| `GET` | `/api/v1/inventory` | Search with `search`, `itemKindId`, `locationId`, `containerId`, and `limit` |
| `GET` | `/api/v1/inventory/{id}` | Get an inventory item |
| `GET` | `/api/v1/item-kinds` | List valid kinds and their known attribute vocabulary |
| `GET` | `/api/v1/places` | List locations and nested containers |
| `GET` | `/api/v1/intake` | List the open intake queue |
| `GET` | `/api/v1/intake/{id}` | Get one queue entry and its draft |
| `POST` | `/api/v1/intake/barcodes` | Queue `{ "code": "..." }` for lookup |
| `POST` | `/api/v1/intake/items` | Queue a complete proposed item for human review |
| `PUT` | `/api/v1/intake/{id}/draft` | Replace a pending draft while retaining its source photo |
| `POST` | `/api/v1/intake/photos` | Queue multipart field `photo`; optional `multipleItems=false` query |
| `POST` | `/api/v1/intake/session` | End the context window and start a new intake session |

Discover identifiers with `item-kinds` and `places` before creating a draft. A minimal request
looks like this:

```json
{
  "name": "USB-C cable",
  "itemKindId": 4,
  "quantity": 2,
  "locationId": 3,
  "attributes": {
    "Connector": "USB-C"
  },
  "priceAmount": 8.99,
  "priceCurrency": "GBP"
}
```

`locationId` and `containerId` are mutually exclusive. Quantity must be positive and the
low-stock threshold cannot be negative. If `priceAmount` is present without a currency,
Stashographer applies the configured default currency and records automation as the evidence
source. Expiry uses ISO `YYYY-MM-DD` in `expiryDate`, with `expiryKind` such as `UseBy` or
`BestBefore`.

For example:

```bash
curl -H "Authorization: Bearer $STASHOGRAPHER_API_KEY" \
  http://localhost:5207/api/v1/inventory?search=cable
```

## MCP tools

Point an MCP client at `/mcp` using Streamable HTTP. When an MCP key exists, configure it as
the bearer token for that connection.

| Tool | Purpose |
|---|---|
| `search_inventory` | Search before proposing duplicates or substitutes |
| `get_item` | Read one current inventory item |
| `list_item_kinds` | Discover valid kinds and known attributes |
| `list_places` | Discover valid locations and containers |
| `list_intake_queue` | Read captures and drafts awaiting work or review |
| `get_intake_item` | Read one queue entry and its current draft |
| `queue_barcode` | Queue a barcode, ISBN, or scanned code |
| `queue_item_draft` | Propose a complete reviewable item |
| `update_intake_draft` | Refine a pending draft without losing its source image |
| `start_intake_session` | Reset the queue context window |

There is intentionally no acceptance, rejection, delete, or arbitrary SQL tool. Photo upload
uses the HTTP API because MCP tool arguments are JSON rather than multipart binary content.

## Audit and trust boundary

Successful API and MCP requests write an audit row containing only the surface, credential
suffix, HTTP method, path without its query string, status code, correlation identifier, and
time. Request bodies, query values, response data, full keys, photos, and item content are not
written to the access audit.

The administrator password, API key, and optional MCP key are separate credentials. Use HTTPS
or a private authenticated overlay network when traffic leaves a trusted LAN; bearer tokens
are otherwise visible to anyone able to observe plaintext HTTP traffic.
