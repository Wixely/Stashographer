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
