---
name: start-infra
description: Start Voidforge development infrastructure. Use ONLY when the user asks to start the database, postgres, infrastructure, docker services, or when tests fail with database connection errors.
---

# Voidforge Infrastructure

Voidforge runs PostgreSQL 16 as its only infrastructure dependency. Postgres shares the devcontainer's network stack via `--network container:`, so `localhost:5432` works transparently — no env vars, no code changes needed.

## Starting Postgres

```bash
bash .claude/start-infra.sh
```

The script:
- Creates a container named `voidforge-postgres` sharing the devcontainer's network stack
- Idempotent — safe to run repeatedly
- Waits for `pg_isready` before returning
- Creates the `voidforge_test` database if missing

Postgres will be reachable at `localhost:5432` — the same host the application code and tests already use.

## Teardown

```bash
docker stop voidforge-postgres
docker rm voidforge-postgres
```

Note: because the container shares the devcontainer's network stack, restarting the devcontainer will orphan the postgres container. Re-run `bash .claude/start-infra.sh` to recreate it.

## Troubleshooting

```bash
docker ps -f name=voidforge-postgres   # is it running?
docker logs voidforge-postgres          # check logs
pg_isready -h localhost -U postgres     # can we reach it?
docker rm -f voidforge-postgres         # nuke and recreate
bash .claude/start-infra.sh
```

The existing `dockerfiles/docker-compose.yml` is a production config — untouched by this setup.
