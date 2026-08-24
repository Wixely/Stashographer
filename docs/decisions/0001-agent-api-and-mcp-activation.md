# ADR 0001: Gate agent API and MCP access at deployment and application levels

- Status: Accepted
- Date: 2026-08-24
- Review: 2027-02-24
- Owner: Maintainers

## Context

Inventory agents need structured access to search existing items, understand location and
attribute context, and add captures quickly. Restored databases and copied development
configuration must not silently expose write-capable automation on a different network. The
interactive intake workflow must also remain the final authority for accepting AI proposals.

## Decision

Stashographer follows Daybreak's automation boundary:

- deployment flags decide whether API and MCP controls and routes can be made available;
- an administrator separately generates credentials and activates each surface in the app;
- API and MCP use shared application operations rather than duplicating domain logic;
- API always requires a rotatable bearer key stored only as a SHA-256 hash;
- MCP requires active API access and may have its own bearer key, but an administrator may
  deliberately omit it for a trusted-network-only endpoint;
- MCP uses stateless Streamable HTTP at `/mcp`, while HTTP contracts are versioned under
  `/api/v1`;
- automation can create and refine queue drafts but cannot accept or reject inventory; and
- access auditing stores request metadata only, never payloads, query values, or full secrets.

## Consequences

Agents get a stable transport-neutral operation set and can use prior queue or inventory
context. Users retain item-by-item verification. Operators must perform two explicit activation
steps and copy generated secrets once. An unauthenticated MCP endpoint remains possible, but
only through an explicit administrator choice and with a visible warning. New automation
operations must preserve the review boundary or require a separate architectural decision.
