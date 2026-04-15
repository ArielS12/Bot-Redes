# TradingBotsPlatform (.NET + Blazor)

Base de proyecto para:

- API REST de bots de trading (`/api/...`)
- Dashboard en Blazor Server
- Integracion de estado de mercado con Binance public API
- Configuracion individual por bot (budget, simbolos, riesgo)
- Persistencia real con SQL Server (EF Core)
- Autenticacion JWT para endpoints sensibles

## Funcionalidades incluidas

- Dashboard con:
  - total de bots
  - bots activos
  - budget total
  - PnL agregado
- CRUD basico de bots:
  - crear
  - editar
  - iniciar/detener
- Vista de mercado:
  - precio actual y variacion 24h por simbolo
- Simulador de ejecucion:
  - servicio en background que ajusta PnL por bot segun variacion del mercado
- Login admin:
  - `POST /api/auth/login` devuelve token JWT
  - crear/editar/iniciar/detener bots requiere autenticacion

## Estructura

- `src/TradingBots.App/Program.cs`: API + host Blazor
- `src/TradingBots.App/Services/`: servicios de bots, mercado, ejecucion
- `src/TradingBots.App/Components/Pages/`: dashboard, bots, market

## Requisitos

- .NET SDK 8.0+
- SQL Server (Express/Developer) en local o remoto

## Ejecucion local

1) Configura `src/TradingBots.App/appsettings.json`:

- `ConnectionStrings:DefaultConnection`
- `Jwt:SecretKey` (cambia por una llave segura real)
- `AdminUser` (usuario y password de login)

2) Ejecuta:

```bash
cd src/TradingBots.App
dotnet restore
dotnet run
```

3) Login y uso:

- Login API: `POST /api/auth/login`
- Usa `Bearer <token>` para endpoints protegidos:
  - `POST /api/bots`
  - `PUT /api/bots/{id}`
  - `POST /api/bots/{id}/start`
  - `POST /api/bots/{id}/stop`

4) Abrir:

- `https://localhost:xxxx/` (dashboard)
- `https://localhost:xxxx/swagger` (API docs)

## Despliegue 24/7 en cloud (Oracle Free)

Se agregaron archivos para despliegue con Docker:

- `src/TradingBots.App/Dockerfile`
- `docker-compose.yml`
- `.env.example`
- `DEPLOY_ORACLE_FREE.md` (guia paso a paso)

Resumen rapido:

1. Copia el proyecto a una VM Oracle Ubuntu.
2. Instala Docker + Compose.
3. Crea `.env` desde `.env.example`.
4. Ejecuta:

```bash
docker compose up -d --build
```

## Despliegue PoC sin tarjeta

Para prueba de concepto (Sandbox + metricas), revisa:

- `DEPLOY_POC_SIN_TARJETA.md`

Incluye despliegue en Render/Koyeb usando `Sqlite` y configuracion lista para testnet.

## Siguientes mejoras recomendadas

- Integracion Binance autenticada (ordenes reales)
- Backtesting engine y paper trading realista
- Alertas (Telegram/Email/Webhooks)
