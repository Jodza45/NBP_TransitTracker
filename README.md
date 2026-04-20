# TransitTracker

TransitTracker je demo aplikacija za pracenje gradskog prevoza u Nisu.

Backend je u .NET 8, frontend je React, a Redis se koristi kao jedina baza. Aplikacija prikazuje stanice na mapi, simulirane autobuse, dolaske na stanicu i pracenje izabranog autobusa.

## Sta je potrebno

- Docker Desktop
- .NET 8 SDK
- Node.js

## Pokretanje backenda

Prvo pripremi OSRM podatke za rute:

```powershell
cd D:\TransitTracker
powershell -ExecutionPolicy Bypass -File .\scripts\prepare-osrm.ps1
```

Zatim pokreni Redis, OSRM i API:

```powershell
cd D:\TransitTracker
docker compose up -d --build
```

Korisni linkovi:

- API Swagger: http://localhost:5000/swagger
- RedisInsight: http://localhost:8001
- Health check: http://localhost:5000/health

## Pokretanje frontenda

```powershell
cd D:\TransitTracker\TransitTracker.Web
npm.cmd install
npm.cmd run dev
```

Frontend se najcesce otvara na:

```text
http://localhost:5173
```

## Podaci

Stanice i linije se ucitavaju iz javnog GTFS izvora za Nis.

Demo vozila se automatski prave pri pokretanju aplikacije. Broj vozila je podesen po liniji i smeru, tako da prometnije linije imaju vise simuliranih autobusa. Vozila se ne pojavljuju sva na istom mestu, vec imaju razmak u polascima.

## Korisne komande

Provera broja aktivnih vozila:

```powershell
docker exec transittracker-redis redis-cli SCARD active-vehicles
```

Provera jedne lokacije vozila:

```powershell
Invoke-RestMethod -Method Get -Uri "http://localhost:5000/api/v1/vehicles/demo-gtfs-1-0-1/location"
```

Rebuild samo API containera:

```powershell
docker compose up -d --build api
```

Zaustavljanje aplikacije bez brisanja podataka:

```powershell
docker compose down
```

Nemoj koristiti `docker compose down -v` ako zelis da sacuvas Redis podatke.

Primer konfiguracije je u `.env.example`.
