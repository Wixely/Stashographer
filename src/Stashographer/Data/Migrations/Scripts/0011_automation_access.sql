-- Optional HTTP API and MCP access. Runtime activation flags remain in the existing
-- key/value Settings table; credentials and metadata-only access events have stable schemas.

CREATE TABLE AgentCredentials (
    Kind         TEXT PRIMARY KEY CHECK (Kind IN ('Api', 'Mcp')),
    SecretHash   TEXT NOT NULL,
    Suffix       TEXT NOT NULL,
    CreatedAtUtc TEXT NOT NULL
);

CREATE TABLE AgentAccessEvents (
    Id               INTEGER PRIMARY KEY AUTOINCREMENT,
    Surface          TEXT NOT NULL,
    CredentialSuffix TEXT NULL,
    Method           TEXT NOT NULL,
    Path             TEXT NOT NULL,
    StatusCode       INTEGER NOT NULL,
    CorrelationId    TEXT NOT NULL,
    OccurredAtUtc    TEXT NOT NULL
);

CREATE INDEX IX_AgentAccessEvents_OccurredAtUtc
    ON AgentAccessEvents (OccurredAtUtc DESC);
