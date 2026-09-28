# Local PostgreSQL MCP Server (agentic AI usage guide)

`tools/postgres-mcp` is a stdio MCP server that lets agentic AI clients
(Claude Code, opencode, Cursor, etc) inspect and query the **local**
SBQR databases (`sbqr_app` + `sbqr_key_vault`) during development. It is a
dev tool only — not part of the SBQR.Api runtime, not in `SBQR.slnx`, and
never deployed.

## One-time setup

1. **Connection strings** — create a `.env` file at the repo root
   (gitignored; see `tools/postgres-mcp/env.example`):

   ```
   ConnectionStrings__sbqr_app=Host=localhost;Port=5432;Database=sbqr_app;Username=postgres;Password=postgres;Include Error Detail=true
   ConnectionStrings__sbqr_key_vault=Host=localhost;Port=5432;Database=sbqr_key_vault;Username=postgres;Password=postgres;Include Error Detail=true
   ```

   Every `ConnectionStrings__*` key becomes a named connection (the suffix
   is the name agents pass to tools). Real environment variables override
   the file; `POSTGRES_MCP_ENV_FILE` points at an explicit path.

2. **Build** (the registration uses `--no-build` so build output never
   corrupts the stdio protocol):

   ```
   dotnet build tools/postgres-mcp -c Release
   ```

3. **Register with your agent client**:

   - **Claude Code** — already done via the repo-root `.mcp.json`; restart
     and approve `sbqr-postgres` via `/mcp`.
    - **opencode** — project config is already in repo-root `opencode.json`
      (repo-scoped: loads only when this repo is the workspace; restart
      opencode after pulling). It runs the built dll directly with a 30 s
      tools timeout so .NET cold start never trips the 5 s default.
      For a manual/global setup, add to `~/.config/opencode/opencode.json`
      with an absolute dll path and `cwd`:

      ```json
      {
        "mcp": {
          "sbqr-postgres": {
            "type": "local",
            "command": ["dotnet", "<abs-path>/tools/postgres-mcp/bin/Release/net10.0/SBQR.PostgresMcp.dll"],
            "cwd": "<abs-path>",
            "timeout": 30000
          }
        }
      }
      ```

## Using it

Just talk to the agent naturally; it discovers and chains the tools:

```
"what tables are in sbqr_app? describe qr_generations, then show me the last 5 rows"
"check whether identity.tenant_configurations has any suspended tenants"
"is the key_vault database empty? show what's in it"
```

### Tools

| Tool                | Purpose                                                        |
| ------------------- | -------------------------------------------------------------- |
| `pg_list_connections` | Configured connections (names, hosts, write mode)           |
| `pg_list_databases`   | Databases on the cluster behind a connection                 |
| `pg_list_tables`      | Tables/views with schema, estimated rows, size (pg_catalog)  |
| `pg_describe_table`   | Columns, constraints (PK/FK/CHECK), indexes                  |
| `pg_query`            | Read-only SQL → markdown table (row-capped, cell-truncated)  |
| `pg_execute`          | Write SQL — **disabled unless explicitly opted in**          |

## Security model (local dev only)

- **Local-host enforcement**: the server refuses to start if any configured
  host is not `localhost` / `127.0.0.1` / `::1`. Extend via
  `POSTGRES_MCP_ALLOWED_HOSTS` if needed (e.g. docker service names).
- **Read-only by default**: `pg_query` runs inside a server-enforced
  `READ ONLY` transaction — writes fail even if the client-side statement
  guard were bypassed.
- **Opt-in writes**: set `POSTGRES_MCP_ALLOW_WRITES=true` in `.env` and
  restart the client to enable `pg_execute` (INSERT/UPDATE/DELETE/DDL).
  Prefer the external migration tool against `db/migrations/*.sql`
  for schema changes.
- **Context safety**: results are row-capped (`POSTGRES_MCP_MAX_ROWS`,
  default 100, ceiling 1000) and long cell values are truncated.
- Connection strings never appear in tool output.

## Tuning (`.env`)

```
POSTGRES_MCP_ALLOW_WRITES=false     # enable pg_execute
POSTGRES_MCP_MAX_ROWS=100           # pg_query row cap (1..1000)
POSTGRES_MCP_TIMEOUT_SECONDS=30     # per-statement timeout (1..600)
POSTGRES_MCP_ALLOWED_HOSTS=         # extra hosts beyond localhost set
```

After changing `.env` or pulling server code changes: rebuild
(`dotnet build tools/postgres-mcp -c Release`) and restart the agent client.
