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

Proyecto principal:

- `src/TradingBots.App`

---

## Decisiones clave tomadas en el chat

1. Stack elegido:
   - Backend: ASP.NET Core (.NET 8 target)
   - Front: Blazor Server
   - DB: SQL Server (por solicitud del usuario)

2. Seguridad:
   - Login admin por JWT (`/api/auth/login`)
   - Endpoints de creación/edición/arranque/parada protegidos

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
- Interactividad Blazor corregida en `Components/App.razor`:
  - `HeadOutlet` y `Routes` con `RenderMode.InteractiveServer`
  - esto resolvió que botones `@onclick` no reaccionaran
- Página `Bots`:
  - botón `Nuevo bot` visible nuevamente
  - botón `Editar` abre modal al activar estado `_showEditorModal`

---

## Estructura y archivos importantes

### Backend / API

- `src/TradingBots.App/Program.cs`
  - registro de servicios
  - creación/ajuste de tablas dinámicas (compatibilidad)
  - mapeo de endpoints

- `src/TradingBots.App/Data/AppDbContext.cs`
  - entidades y mapeo EF Core

- `src/TradingBots.App/Models/BotModels.cs`
  - modelos principales (bots, sugerencias, performance, auth, etc.)

### Servicios de dominio

- `src/TradingBots.App/Services/BotService.cs`
  - ciclo de simulación de trading
  - pnl realizado/no realizado
  - escalado de budget en bots auto
  - **aclaración importante**: no maneja inventario/posición real por símbolo (lógica simulada)

- `src/TradingBots.App/Services/BinanceMarketService.cs`
  - lectura de mercado Binance
  - soporte de análisis completo con `*`

- `src/TradingBots.App/Services/MarketAdvisorService.cs`
  - generación de sugerencias + estrategia sugerida

- `src/TradingBots.App/Services/AutoTraderService.cs`
  - creación automática de bots desde sugerencias

- `src/TradingBots.App/Services/BotExecutionBackgroundService.cs`
  - orquestación periódica (cada 10s aprox)

- `src/TradingBots.App/Services/RuntimeStatusService.cs`
  - estado última ejecución del auto-creador

### Front / Blazor

- `src/TradingBots.App/Components/Pages/Home.razor`
  - dashboard principal
  - sugerencias analista
  - indicador última ejecución auto-creador
  - performance por bot

- `src/TradingBots.App/Components/Pages/Bots.razor`
  - tabla de bots
  - estrategia, tipo auto/manual, pnl diario/acumulado
  - acceso a detalle por bot

- `src/TradingBots.App/Components/Pages/BotDetail.razor`
  - detalle de bot + historial de compras/ventas

- `src/TradingBots.App/Components/Pages/Settings.razor`
  - configuración Binance (sandbox/producción + keys)

---

## Endpoints relevantes

### Auth

- `POST /api/auth/login`

### Bots

- `GET /api/bots`
- `GET /api/bots/{id}`
- `POST /api/bots` (auth)
- `PUT /api/bots/{id}` (auth)
- `POST /api/bots/{id}/start` (auth)
- `POST /api/bots/{id}/stop` (auth)
- `GET /api/bots/{id}/trades`  <-- detalle de operaciones
- `GET /api/bots/performance`  <-- pnl diario/acumulado por bot

### Mercado / analista

- `GET /api/market/overview?symbols=...`
- `GET /api/advisor/suggestions`
- `GET /api/dashboard/summary`

### Configuración exchange

- `GET /api/settings/binance`
- `PUT /api/settings/binance` (auth)

---

## Cómo ejecutar localmente (resumen)

1. Ir al proyecto:
   - `cd C:\Users\samue\TradingBotsPlatform\src\TradingBots.App`
2. Ejecutar:
   - `dotnet run`
3. Abrir:
   - `http://localhost:5001/`
   - `http://localhost:5001/swagger`

Nota: se estaba usando `ASPNETCORE_ENVIRONMENT=Development` y `ASPNETCORE_URLS=http://localhost:5001`.

---

## Pendientes recomendados (siguientes pasos)

1. Seguridad de secretos:
   - cifrar `ApiSecret` en base de datos
   - no guardar secretos en texto plano

2. Estrategia real:
   - reemplazar simulación por lógica técnica real (EMA/RSI/MACD)
   - incluir validación por volumen y liquidez

3. Gestión de riesgo avanzada:
   - límites globales por cuenta
   - límite por correlación de activos
   - circuit breaker por volatilidad extrema

4. Mejoras de UI:
   - filtros por estrategia y tipo de bot
   - filtros de rango temporal en detalle de trades
   - exportar CSV de operaciones

5. Producción:
   - logging estructurado
   - alertas (Telegram/email/webhook)
   - health checks y supervisión

6. Motor de ejecución (prioridad funcional):
   - implementar posiciones reales por símbolo (cantidad, costo promedio)
   - separar señales de `BUY`/`SELL` de la liquidación de PnL simulado
   - definir regla explícita de salida:
     - venta parcial vs venta total
     - take-profit/stop-loss sobre posición real
   - registrar `TradeExecution.Quantity` variable (no fija en `0.001`)
   - permitir estrategia de cierre configurable por bot

---

## Posibles problemas conocidos y cómo resolver rápido

1. Build bloqueado por ejecutable en uso:
   - detener proceso `TradingBots.App.exe` antes de compilar

2. Swagger no aparece:
   - verificar entorno `Development`

3. SQL no conecta:
   - revisar cadena de conexión SQL Server en `appsettings.json`

4. Botones de UI visibles pero sin acción:
   - verificar que `Components/App.razor` tenga renderizado interactivo:
     - `HeadOutlet @rendermode="Microsoft.AspNetCore.Components.Web.RenderMode.InteractiveServer"`
     - `Routes @rendermode="Microsoft.AspNetCore.Components.Web.RenderMode.InteractiveServer"`

---

## Comportamiento actual del bot (validado)

Estado actual del motor en `BotService.TickBotsAsync`:

- El ciclo corre cada `10s` (orquestado por `BotExecutionBackgroundService`).
- No existe una cartera/posición acumulada real por símbolo.
- Se actualiza `UnrealizedPnlUsdt` por simulación.
- Solo se registra una operación cuando:
  - `abs(UnrealizedPnlUsdt) > MaxPositionPerTradeUsdt * 0.15`
- La operación se marca:
  - `BUY` si `realized < 0`
  - `SELL` si `realized >= 0`
- No hay venta de "todo lo comprado", porque no se almacena inventario real.
- `Quantity` actualmente es fija en `0.001`.

Conclusión práctica:

- Si el bot acumula resultado negativo, puede registrar varias compras seguidas.
- Vende cuando el cierre parcial simulado da positivo.
- No liquida una posición total en una sola transacción porque esa posición no existe como entidad persistida hoy.

---

## Prompt sugerido para retomar desde aquí

Usar este texto en un nuevo chat:

> "Retomemos el proyecto en `C:\\Users\\samue\\TradingBotsPlatform`.  
> Lee `CONTINUIDAD_CHAT.md` y continúa desde los pendientes.  
> Primero implementa: [aquí pones la prioridad, por ejemplo 'cifrado de ApiSecret y test de conexión Binance'].
> Mantén compatibilidad con SQL Server y valida con build al finalizar."

---

## Nota final

Este documento resume toda la conversación y el estado funcional en un formato práctico para continuidad.
Si el proyecto evoluciona, actualizar este archivo al final de cada sesión.
