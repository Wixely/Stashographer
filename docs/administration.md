# Administrator access

Stashographer keeps household workflows available without an account while protecting
application configuration with one deployment-supplied administrator password. This follows
Daybreak's low-friction administrator model; it is intended for a household or trusted-network
deployment, not as multi-user identity management.

## Sign in

Open **Settings** or `/settings`. Unauthenticated browsers are redirected to `/admin/login`.
The Docker image and local launch profiles use `admin` as a convenience default. Override it
with an environment variable before relying on it as a security control:

```text
STASHOGRAPHER_ADMIN_PASSWORD=replace-with-a-private-password
```

The application refuses to start when the variable is missing. It hashes the configured value
in memory and compares sign-in attempts in fixed time; neither the password nor its hash is
written to SQLite. Changing the variable and restarting immediately invalidates sessions issued
for the previous password.

## Session security

Successful sign-in creates a 12-hour, sliding, HTTP-only, same-site cookie. Cookie-signing keys
are stored under `Stashographer:DataProtectionKeysPath` (`App_Data/keys` from source and
`/data/keys` in Docker) so sessions survive ordinary restarts. Login and logout forms use
antiforgery tokens, external return URLs are rejected, and the login endpoint permits five
attempts per minute per application instance.

This administrator gate also protects API and MCP activation and credential controls. API and
MCP clients use their own rotatable bearer credentials; the browser administrator cookie is
never reused as an automation credential. See [API and MCP automation](automation.md) for the
two-stage activation model and supported operations.

## Browser image uploads

Camera and library selections use `DnaX.Uploads` rather than carrying image bytes over the
Blazor Server SignalR circuit. The native input and upload queue are browser-owned: images are
persisted in IndexedDB, transferred in 1 MiB verified chunks, and resumed from the server's
acknowledged offset after a disconnect or reload. Files remain limited to the configured image
size (20 MiB by default), and completed transport sessions expire from staging after seven days.

DNAX transport completion is deliberately separate from Stashographer business completion.
After DNAX durably accepts the bytes, Stashographer sanitizes and re-encodes the image, stores
it under the normal image root, and creates the requested intake/modify record. Both boundaries
use the same random upload ID idempotently, so losing either HTTP response cannot duplicate an
image or queue entry. Mutations remain same-origin, antiforgery-protected, owner-isolated, and
concurrency-limited. A protected, random upload-owner cookie supplies DNAX's stable owner without
requiring household users to sign in as the administrator.
