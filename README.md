# TransitTracker

Real-time public transit tracking system for Nis, built with .NET 8 Clean Architecture, Redis-only backend, and React + Leaflet frontend.

## Stack

- Backend: ASP.NET Core 8, MediatR, FluentValidation, SignalR, Serilog
- Storage: Redis Stack (GEO, Streams, Pub/Sub, Hash/Set)
- Frontend: React + TypeScript + Vite + Leaflet
- Orchestration: Docker Compose

## Repository structure

- `TransitTracker.API`
- `TransitTracker.Application`
- `TransitTracker.Infrastructure`
- `TransitTracker.Domain`
- `TransitTracker.Web`
- `docker-compose.yml`

## Environment setup

1. Copy environment template:

```powershell
cd D:\TransitTracker
Copy-Item .env.example .env
```

2. Optional: edit `.env` ports/values.

Default values are safe for local development.

## Run backend with Docker

```powershell
cd D:\TransitTracker
docker compose up -d --build redis api
```

- Swagger: [http://localhost:5000/swagger](http://localhost:5000/swagger)
- Health: [http://localhost:5000/health](http://localhost:5000/health)
- RedisInsight: [http://localhost:8001](http://localhost:8001)

## Run frontend

```powershell
cd D:\TransitTracker\TransitTracker.Web
npm.cmd install
npm.cmd run dev
```

Vite will print the frontend URL (usually `http://localhost:5173`).

## Safe-to-commit checklist

Before pushing to Git, run:

```powershell
cd D:\TransitTracker

# 1) Verify ignored files (env/secrets/build outputs should not be tracked)
git status --short

# 2) Check for accidental secrets (simple pattern scan)
git grep -n -I -E "(password|passwd|secret|token|apikey|api_key|connectionstring|private key)" -- . ":(exclude)package-lock.json"

# 3) Build checks
dotnet build .\TransitTracker.sln
cd .\TransitTracker.Web
npm.cmd run build
```

If command #2 reports real secrets, remove or move them to `.env` (never commit real credentials).

## Git ignore policy

Root `.gitignore` already excludes:

- `.env` and local env files
- `bin/`, `obj/`, `dist/`, `node_modules/`
- local caches (`.dotnet`, `.nuget`, `.npm-cache`)
- logs and binlogs

Commit only source code, templates (like `.env.example`), and docs.

## GTFS route import (Nis)

To map each line to exact station sequence (and avoid showing unrelated buses at a station), import official Nis GTFS:

```powershell
Invoke-RestMethod -Method Post -Uri "http://localhost:5000/api/v1/gtfs/import-nis"
```

If you also want demo vehicles auto-bound to imported lines for testing arrivals:

```powershell
Invoke-RestMethod -Method Post -Uri "http://localhost:5000/api/v1/gtfs/import-nis?force=true"
```
