-- App settings editable at runtime from the Settings page (e.g. AI configuration inside
-- Docker, where appsettings/env vars can't be changed without recreating the container).
-- Values stored here take precedence over configuration once saved.

CREATE TABLE Settings (
    Key   TEXT PRIMARY KEY,
    Value TEXT NOT NULL
);
