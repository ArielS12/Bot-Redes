# Continuidad de conversación - TradingBotsPlatform

## Objetivo de este documento

Este archivo sirve para retomar el proyecto desde este punto, aunque se cierre el chat o se apague el ordenador.

Incluye:

- resumen de lo construido
- decisiones técnicas tomadas
- estado actual de ejecución
- endpoints y pantallas disponibles
- pendientes recomendados
- prompt sugerido para retomar el trabajo

## Actualizacion anti-perdidas (2026-11-17c)

Usuario confirmo: **"solo genera perdidas"**. Se refuerza expectancy (no volumen):

- Soft-BE HTF floor **1.10%**; SL/TP/trail AutoPilot **1.35 / 3.2 / 1.8 / 0.9**.
- Invalidacion temprana HTF **-0.55%** / **480 min**; bounce hold min **120 min**.
- Entrada: RSI max **48** + expansion minima MACD.
- AutoTrader sync params HTF en bots sin posicion; kill-switch n>=8 / PF&lt;1 / neto&lt;=-1.
- Gate **sin soft-pass**; backtest HTF ya no time-stopa winners solo por MaxHold.
- Concurrencia flota = `SafeLiveMaxAutoBots` (2); gate cada 6h; readiness con gate.

## Actualizacion auditoria expectancy (2026-11-17b)

Diagnostico: expectancy estructural negativa (soft-BE 0.45% vs SL ~1%+fees) + bugs de gestion/entradas.

Parches previos: soft-BE >=1%, SL efectivo sin floor 1.2 forzado, cooldown no salta posicion abierta, budget HTF 12/12, AutoTrader solo BUY+quality (sin seed), klines sin vela abierta, ML sin shadow, GlobalMaxDailyLoss min 1.

**Importante:** mejora R:R; **no garantiza profit**. Paper/sandbox 2-4 semanas; mirar `/api/bots/analytics` antes de subir size Live.

## Actualizacion auditoria produccion (2026-11-17)

Diagnostico previo: flota HTF sobre-restringida. Desbloqueo: gate periodico, concurrencia 2, readiness.

**Estado operativo real (regimen actual):**

- AutoPilot: solo **PullbackHtf** en **BTCUSDT/ETHUSDT**, cap **2** bots, size **12 USDT**.
- Posicion real persistida (`PositionQuantity`, `AverageEntryPrice`); NO es el motor simulado antiguo.
- Gate FAIL → `MaxAutoBots=0` hasta que una reevaluacion pase.
- `PublicAppMode.Enabled=true` (JWT deshabilitado): no exponer Live a Internet sin otra capa.

Diagnostico rapido en runtime:

1. `GET /api/backtest/gate-status` y `GET /api/system/readiness`
2. Settings → `MaxAutoBots` / kill switch / ExecutionMode
3. `GET /api/bots/signals` → motivo ESPERANDO (D1 / MTF / portfolio)
4. `GET /api/bots/analytics` → PF / expectancy / semaforo solidez

## Convencion de trabajo (2026-05-10)

- **Fuente de verdad de codigo:** toda implementacion nueva o correccion se hace en **`Bot-Redes/src/TradingBots.App`**. La carpeta `src/TradingBots.App` queda como copia legacy: no editarla salvo peticion explicita de sincronizar ambas copias.
- **Este documento:** mantener actualizado este `CONTINUIDAD_CHAT.md` (raiz del repo) cuando cambien flujos, rutas, endpoints o despliegue.
- **Despliegue / PoC:** la aplicacion desplegada en cloud (p. ej. Render) se alinea con el proyecto bajo **Bot-Redes**.

### Modo publico y seguridad (Bot-Redes)

- `Bot-Redes/src/TradingBots.App/PublicAppMode.cs`: `PublicAppMode.Enabled` (por defecto `true`).
- Con modo publico: registro JWT en `Program.cs` comentado; `POST /api/auth/login` comentado; `UseAuthentication` / `UseAuthorization` comentados; layout sin barra de sesion; `Login.razor` solo aviso; `ClientAuthSession` considera autenticado y marca `SessionHydrated` para no bloquear `Bots.razor`.
- **Riesgo:** sin otra capa (VPN, reverse proxy con auth), no exponer asi a Internet si hay credenciales de exchange o Live.

## Actualizacion rapida (2026-07-07)

Mejoras aplicadas en `Bot-Redes/src/TradingBots.App` tras auditoria OSS + diagnostico produccion (`bot-redes.onrender.com`):

- **Desbloqueo de flota (deadlock 30/30):**
  - `FleetCapacityHelper`: solo cuentan cupo los bots con posicion, trade reciente (24h) o gracia de arranque (6h).
  - `AutoTraderService`: libera slots tras 48h sin trades si el simbolo ya no esta en el top del analista; rebalanceo usa `LastRunningStartedAtUtc` (no `UpdatedAtUtc`).
  - `BotSupervisorService`: detiene bots auto sin ningun trade tras `NeverTradedStopHours` (default 36h).
  - Reciclaje prioriza bots detenidos con mayor `RealizedPnlUsdt`.

- **Filtros de entrada adaptativos (`EntryFilters`):**
  - Majors BTC/ETH/SOL/BNB: volumen 24h min 300k, volumen relativo min 0.28.
  - Alts: volumen 24h min 500k, volumen relativo min 0.35.

- **Riesgo portfolio (`PortfolioRiskService`):**
  - PnL gate global diario (`GlobalMaxDailyLossUsdt`, default 15 USDT).
  - BTC crash gate (bloquea LONG en alts si BTC 24h <= -`BtcCrashGatePercent`, default 3%).
  - Limite de posiciones abiertas en alts (`MaxConcurrentAltPositions`, default 4).

- **Salidas mejoradas (`BotService`):**
  - Stop-loss dinamico por ATR (`1.5 x AtrPercent`, suelo 1.2%).
  - Breakeven al 30% del camino al TP.
  - Ya no se actualiza `UpdatedAtUtc` en cada tick (evita romper rebalanceo).

- **ML shadow tracking (`TradeMlService`):**
  - `RecordShadowSignalAsync` en rechazos (portfolio/ML).
  - `ResolveShadowSignalsAsync` forward-walk hipotetico (TP 1.5% / SL 1.85% / timeout 180 min).
  - Columnas nuevas en `MlTradeObservations`: `IsShadow`, `RejectReason`.

- **Settings nuevos en `BinanceSettings`:** `GlobalMaxDailyLossUsdt`, `BtcCrashGatePercent`, `MaxConcurrentAltPositions`, `NeverTradedStopHours`.

- Build verificado: `dotnet build Bot-Redes/src/TradingBots.App/TradingBots.App.csproj -c Release` OK.

**Despliegue:** hacer push de `Bot-Redes` a Render para activar en produccion.

## Actualizacion rapida (2026-04-09)

Estado real verificado en esta fecha:

- Aplicacion ejecutando en `Development` en `http://localhost:5001`.
- Integracion Binance en `Sandbox` operativa con guardas de `LiveEnabledByChecklist` y `GlobalKillSwitch`.
- Minimo operativo de compra ajustado a `10 USDT` en el motor (`BotService`).
- Auto-creador actualizado:
  - nuevos bots auto se crean con `MaxPositionPerTradeUsdt = 10` y `BudgetUsdt = 50`.
- Capacidad de bots auto aumentada a `MaxAutoBots = 20` (opcion B aplicada).
- Diagnostico actual: los bots pueden quedar en `ESPERANDO` por filtros de entrada estrictos
  (`liquidez`, `volumen relativo`, `confirmacion multi-timeframe 5m/15m`), aunque el analista genere
  sugerencias `BUY`.
- Base de datos verificada: sin operaciones ejecutadas recientes (`Trades = 0`) en el ultimo chequeo.

Nota importante:

- El campo `LastExecutionError` en algunos bots puede mostrar errores historicos de intentos anteriores
  con monto `2 USDT`. No implica que el motor actual siga operando con 2; el minimo vigente es `10 USDT`.

## Integracion Go-Live Cuant (2026-04-09)

Se aplicaron ajustes para aumentar la calidad estadistica de resultados en ventanas de 3 meses:

- **Salidas de riesgo corregidas**:
  - `stop-loss` y `time-stop` ya pueden cerrar posicion aunque no haya profit.
  - Las salidas tacticas (`senal/TP/trailing`) mantienen condicion de profit.

- **Control adaptativo con muestra minima**:
  - El auto-escalado y el throttling por edge negativo ahora requieren muestra minima de `100` cierres (`SELL`) por bot.
  - Se agregaron cooldowns de ajuste para evitar sobre-optimizar:
    - `LastAutoScaleUtc` (cooldown 6h).
    - `LastRiskAdjustmentUtc` (cooldown 6h).

- **Rotacion anti-ruido (histeresis)**:
  - Se mantiene `MinActiveBeforePause` y se añade criterio de `3 ciclos fuera del top` antes de pausar por rebalanceo.
  - Se registra `OutOfTopCycles` por bot para reducir "flapping".

- **Cohortes mas limpias en auto-creacion**:
  - Se excluyen pares `stable-stable` (ej. USDC/USDT/FDUSD entre si) del auto-creador para evitar sesgo en metricas globales.

- **Regla automatica de solidez (semaforo)**:
  - Integrada en `GET /api/bots/analytics` y visible en `Bots.razor`.
  - Criterios:
    - `VERDE`: `ClosedTrades >= 200`, `ProfitFactor > 1.2`, expectancy por trade positiva.
    - `AMARILLO`: `ClosedTrades >= 100` y `ProfitFactor >= 1.0`.
    - `ROJO`: resto de casos.
  - Se expone ademas `SolidityScore` (0..100 aprox) y `SolidityReason`.

- **Meta-controlador autonomo de parametros (fase 1 conservadora)**:
  - Nuevo servicio: `ControlAutotuneService` (ejecucion cada ciclo, ajuste maximo 1 vez cada 24h).
  - Ajusta automaticamente en `BinanceSettings`:
    - `SupervisorInactiveMinutes` (60..240)
    - `RebalanceOutOfTopCycles` (2..6)
    - `MinActiveBeforePauseMinutes` (10..90)
    - `MinStoppedBeforeReactivateMinutes` (2..30)
  - Reglas de ajuste:
    - muestra baja (`<30 SELL`/7d): modo conservador (menos churn).
    - edge debil (`PF<1` o expectancy<0): endurece.
    - edge bueno (`PF>=1.2`, expectancy>0) con poca actividad: relaja gradualmente.
  - Guardrails:
    - `AutoControlTuningEnabled` (on/off)
    - `LastAutoControlTuneUtc` (cooldown hard de 24h)
    - cambios por pasos pequenos y con limites duros.

## Despliegue PoC sin tarjeta

Se preparo ruta de despliegue para pruebas de concepto (sin tarjeta, sin garantia 24/7 estricta):

- `render.yaml` (Blueprint en Render)
- soporte `DB_PROVIDER=Sqlite` en `Program.cs`
- guia operativa: `DEPLOY_POC_SIN_TARJETA.md`

Objetivo: correr en cloud con API key de Sandbox para obtener metricas y validar comportamiento.

Campos nuevos en `Bots` (compat SQL incluida en `Program.cs`):

- `LastAutoScaleUtc` (`datetime2`, nullable)
- `LastRiskAdjustmentUtc` (`datetime2`, nullable)
- `OutOfTopCycles` (`int`, default 0)

---

## Resumen del proyecto (estado actual)

Se construyó una plataforma en `.NET + Blazor` con `SQL Server` para gestionar bots de trading y un bot analista de mercado.

Componentes principales:

- API REST (Minimal API)
- Dashboard web en Blazor Server
- Persistencia con EF Core + SQL Server
- Integración de mercado Binance (actualmente pública)
- Autenticación JWT para acciones protegidas
- Bot analista + auto-creación de bots de trading
- Auto-escalado progresivo de budget en bots auto

Ruta del proyecto:

- `C:\Users\samue\TradingBotsPlatform`

Proyecto principal (donde se trabaja):

- `Bot-Redes/src/TradingBots.App`

Copia legacy (no editar salvo sincronizacion explicita):

- `src/TradingBots.App`

---

## Decisiones clave tomadas en el chat

1. Stack elegido:
   - Backend: ASP.NET Core (.NET 8 target)
   - Front: Blazor Server
   - DB: SQL Server (por solicitud del usuario)

2. Seguridad:
   - Login admin por JWT (`/api/auth/login`) cuando JWT esta activo
   - Endpoints de creación/edición/arranque/parada protegidos con JWT en el arquetipo legacy; en **Bot-Redes** con modo publico no se exige token (ver convencion arriba)

3. Configuración de Binance:
   - Configurable desde dashboard (`/settings`)
   - Persistida en base de datos (`BinanceSettings`)
   - Entorno seleccionable: `Sandbox` o `Production`

4. Bot analista:
   - Analiza mercado completo (no solo símbolos de bots manuales)
   - Genera sugerencias con estrategia sugerida (`Momentum` / `Pullback`)

5. Auto-trader:
   - Crea bots automáticamente según sugerencias válidas
   - Budget inicial de prueba: `10 USDT`
   - Escalado gradual de budget si hay profit realizado
   - Evita duplicar bots auto por símbolo
   - Capacidad de bots auto activos limitada

6. Observabilidad en UI:
   - Última ejecución del auto-creador
   - Profit/Pérdida por bot: diario y acumulado
   - Estrategia sugerida y estrategia activa visibles

7. Detalle por bot:
   - Vista de trades (compras/ventas) ordenados de más reciente a más antiguo

---

## Estado funcional confirmado

En la última validación:

- App levantada en `Development`
- URL: `http://localhost:5001`
- `Dashboard` responde
- `Swagger` responde
- Endpoint de sugerencias responde
- Endpoint de rendimiento por bot responde
- Auto-creación de bots corregida y funcionando
- Interactividad Blazor corregida en `Bot-Redes/src/TradingBots.App/Components/App.razor`:
  - `HeadOutlet` y `Routes` con `RenderMode.InteractiveServer`
  - esto resolvió que botones `@onclick` no reaccionaran
- Página `Bots`:
  - botón `Nuevo bot` visible nuevamente
  - botón `Editar` abre modal al activar estado `_showEditorModal`

---

## Estructura y archivos importantes

### Backend / API

- `Bot-Redes/src/TradingBots.App/Program.cs`
  - registro de servicios
  - creación/ajuste de tablas dinámicas (compatibilidad)
  - mapeo de endpoints

- `Bot-Redes/src/TradingBots.App/Data/AppDbContext.cs`
  - entidades y mapeo EF Core

- `Bot-Redes/src/TradingBots.App/Models/BotModels.cs`
  - modelos principales (bots, sugerencias, performance, auth, etc.)

### Servicios de dominio

- `Bot-Redes/src/TradingBots.App/Services/BotService.cs`
  - ciclo de trading real (Paper/Live) con posicion persistida
  - pnl realizado/no realizado, SL/TP/trailing, circuit breakers
  - escalado de budget en bots auto (requiere muestra minima de SELL)

- `Bot-Redes/src/TradingBots.App/Services/BinanceMarketService.cs`
  - lectura de mercado Binance
  - soporte de análisis completo con `*`

- `Bot-Redes/src/TradingBots.App/Services/MarketAdvisorService.cs`
  - generación de sugerencias + estrategia sugerida

- `Bot-Redes/src/TradingBots.App/Services/AutoTraderService.cs`
  - creación automática de bots desde sugerencias

- `Bot-Redes/src/TradingBots.App/Services/BotExecutionBackgroundService.cs`
  - orquestación periódica (cada 10s aprox)

- `Bot-Redes/src/TradingBots.App/Services/RuntimeStatusService.cs`
  - estado última ejecución del auto-creador

### Front / Blazor

- `Bot-Redes/src/TradingBots.App/Components/Pages/Home.razor`
  - dashboard principal
  - sugerencias analista
  - indicador última ejecución auto-creador
  - performance por bot

- `Bot-Redes/src/TradingBots.App/Components/Pages/Bots.razor`
  - tabla de bots
  - estrategia, tipo auto/manual, pnl diario/acumulado
  - acceso a detalle por bot

- `Bot-Redes/src/TradingBots.App/Components/Pages/BotDetail.razor`
  - detalle de bot + historial de compras/ventas

- `Bot-Redes/src/TradingBots.App/Components/Pages/Settings.razor`
  - configuración Binance (sandbox/producción + keys)

---

## Endpoints relevantes

Referencia: `Bot-Redes/src/TradingBots.App/Program.cs` (revisión 2026-05-10). Los marcados **(auth)** exigen JWT **solo cuando** el login y el middleware JWT estén activos (`PublicAppMode.Enabled = false` y bloques descomentados). Con **modo publico** activo en Bot-Redes, esas rutas siguen existiendo pero **no** comprueban token.

### Auth

- `POST /api/auth/login` — cuerpo `LoginRequest`; devuelve token y expiración (**comentado en Bot-Redes** mientras modo publico).

### Bots

- `GET /api/bots` — lista completa (público).
- `GET /api/bots/paged?page=&pageSize=` — paginación usada por `Bots.razor` (público).
- `GET /api/bots/{id}` — un bot por GUID (público).
- `POST /api/bots` **(auth)** — crear.
- `PUT /api/bots/{id}` **(auth)** — actualizar.
- `POST /api/bots/{id}/start` **(auth)** — arrancar.
- `POST /api/bots/{id}/stop` **(auth)** — detener.
- `POST /api/bots/{id}/auto-block` **(auth)** — bloquear auto-reanudación.
- `POST /api/bots/{id}/auto-unblock` **(auth)** — desbloquear auto-reanudación.
- `POST /api/bots/{id}/force-sell` — venta forzada (respuesta JSON con `outcome`; **solo en Bot-Redes** en esta revision del documento).
- `GET /api/bots/{id}/trades` — historial de operaciones del bot (público).
- `GET /api/bots/performance?botIds=` — PnL diario/acumulado; `botIds` opcional, GUIDs separados por coma (público).
- `GET /api/bots/signals?botIds=` — diagnóstico de señal / motivo de espera (público).
- `GET /api/bots/analytics?botIds=` — métricas, semáforo de solidez (`VERDE`/`AMARILLO`/`ROJO`) (público).

### Mercado / analista / dashboard

- `GET /api/market/overview?symbols=` — tickers; `symbols` lista separada por comas (público).
- `GET /api/advisor/suggestions` — últimas sugerencias del analista (público).
- `GET /api/dashboard/summary` — tarjetas dashboard, mercado, sugerencias, `botPerformance`, modo ejecución (público).
- `GET /api/dashboard/trade-kpis?dateFrom=yyyy-MM-dd&dateTo=yyyy-MM-dd` — KPIs agregados y `byBot` en rango **UTC** (público).

### ML (filtro de trades / sombra)

- `GET /api/ml/summary` — estado del modelo, muestras, shadow mode (público).
- `GET /api/ml/diagnostics` — diagnóstico extendido ML (público).

### Auditoría y sistema

- `GET /api/audit/orders?take=` — últimos eventos de órdenes auditadas; `take` acotado 1..500 (público).
- `GET /api/system/readiness` — salud, bots en marcha, checklist live (público).

### Configuración exchange

- `GET /api/settings/binance` — vista de ajustes (sin secretos en claro según implementación del view) (público).
- `PUT /api/settings/binance` **(auth)** — actualizar conexión y flags.
- `GET /api/settings/binance/account-summary` **(auth)** — resumen cuenta exchange.
- `GET /api/settings/binance/health` **(auth)** — comprobación de conectividad/salud.
- `POST /api/settings/binance/arm-live` **(auth)** — checklist para armar trading live.

---

## Cómo ejecutar localmente (resumen)

1. Ir al proyecto:
   - `cd C:\Users\samue\TradingBotsPlatform\Bot-Redes\src\TradingBots.App`
2. Ejecutar:
   - `dotnet run`
3. Abrir:
   - `http://localhost:5001/`
   - `http://localhost:5001/swagger`

Nota: se estaba usando `ASPNETCORE_ENVIRONMENT=Development` y `ASPNETCORE_URLS=http://localhost:5001`.

---

## Pendientes recomendados (siguientes pasos)

1. Seguridad:
   - reactivar JWT (`PublicAppMode.Enabled = false`) antes de Live con capital real
   - cifrar `ApiSecret` en base de datos

2. Edge / actividad (si tras el parche sigue sin trades):
   - alinear `MarketAdvisorService` a scoring 15m/1h (hoy sigue 1m-centrico)
   - exponer contadores de bloqueo agregados en dashboard
   - ampliar universo (BNB/SOL) solo tras gate VERDE estable

3. Calidad de codigo:
   - tests unitarios de estrategias, sizing y gate
   - eliminar dead code (`PassesQualityGate` no se llama)

4. Mejoras de UI:
   - filtros por estrategia y tipo de bot
   - exportar CSV de operaciones
   - panel de gate/readiness visible en Home

5. Producción:
   - alertas (Telegram/email/webhook) cuando gate FAIL o kill-switch
   - logging estructurado

---

## Posibles problemas conocidos y cómo resolver rápido

1. Build bloqueado por ejecutable en uso:
   - detener proceso `TradingBots.App.exe` antes de compilar

2. Swagger no aparece:
   - verificar entorno `Development`

3. SQL no conecta:
   - revisar cadena de conexión SQL Server en `appsettings.json`

4. Botones de UI visibles pero sin acción:
   - verificar que `Bot-Redes/src/TradingBots.App/Components/App.razor` tenga renderizado interactivo:
     - `HeadOutlet @rendermode="Microsoft.AspNetCore.Components.Web.RenderMode.InteractiveServer"`
     - `Routes @rendermode="Microsoft.AspNetCore.Components.Web.RenderMode.InteractiveServer"`

---

## Comportamiento actual del bot (validado 2026-11-17)

Estado del motor en `BotService.TickBotsAsync` (orquestado cada ~10s):

- Posicion real: `PositionQuantity`, `AverageEntryPrice`, `PositionSymbol`.
- Entrada: senal estrategia + MTF + regimen corto + D1 + EntryFilters + PortfolioRisk (+ ML opcional).
- Salida: SL/ATR, breakeven, TP, trailing, time-stop, invalidacion de estructura.
- Paper: fill al last price; Live: `BinanceTradeExecutionService` MARKET.
- PnL realizado resta ~20 bps round-trip estimado (fees+slippage).
- AutoPilot HTF: BTC/ETH, size 12 USDT, max 2 bots / hasta 2 posiciones abiertas.

---

## Prompt sugerido para retomar desde aquí

Usar este texto en un nuevo chat:

> "Retomemos el proyecto en `C:\\Users\\samue\\TradingBotsPlatform`.  
> Lee `CONTINUIDAD_CHAT.md` y trabaja **solo** en `Bot-Redes\\src\\TradingBots.App`.  
> Actualiza `CONTINUIDAD_CHAT.md` al terminar.  
> Primero implementa: [prioridad].  
> Valida con `dotnet build` sobre `Bot-Redes\\src\\TradingBots.App\\TradingBots.App.csproj`."

---

## Nota final

Este documento resume toda la conversación y el estado funcional en un formato práctico para continuidad.
Si el proyecto evoluciona, actualizar este archivo al final de cada sesion; el codigo activo vive en **Bot-Redes**.
