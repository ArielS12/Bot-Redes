# Despliegue PoC sin tarjeta (Sandbox)

Esta guia deja la app funcionando para pruebas de concepto y metricas, usando `Sandbox` de Binance.

## Donde registrarte

Opciones sin tarjeta (segun pais/politica vigente):

- [Render](https://render.com/) (recomendado para PoC rapido)
- [Koyeb](https://www.koyeb.com/)

> Nota: en planes free puede haber suspension por inactividad o limites de uso.

## Repositorio listo

Ya estan preparados:

- `render.yaml` (infra declarativa para Render)
- `Dockerfile` en `src/TradingBots.App/`
- soporte DB provider (`SqlServer` o `Sqlite`) via `DB_PROVIDER`

## Paso a paso en Render

1. Sube este proyecto a GitHub/GitLab.
2. En Render: **New +** -> **Blueprint**.
3. Conecta tu repositorio y selecciona rama.
4. Render detecta `render.yaml` y crea el servicio.
5. Espera a que termine el build/deploy.

## Variables importantes (ya incluidas en render.yaml)

- `DB_PROVIDER=Sqlite`
- `Database__Provider=Sqlite`
- `ConnectionStrings__SqliteConnection=Data Source=tradingbots.db`
- `Jwt__SecretKey` (autogenerada)
- `AdminUser__Password` (autogenerada)

## Primer login

Cuando termine el deploy:

1. Abre `https://<tu-servicio>.onrender.com/swagger`
2. Recupera el password admin desde variables del servicio (Render dashboard).
3. Login en `POST /api/auth/login`.

## Configuracion recomendada para PoC

En `/settings`:

- `Environment = Sandbox`
- `ExecutionMode = Live` (sobre testnet)
- `AutoControlTuningEnabled = true`
- API keys solo de testnet

## Limitaciones de PoC en free

- Con SQLite en free tier, el filesystem puede no ser persistente en redeploy/restart.
- Si necesitas persistencia real de metricas, mueve DB a un servicio externo (Neon/Supabase/Postgres) en siguiente fase.

## Checklist final

- [ ] Servicio arriba y responde `/swagger`
- [ ] Login admin correcto
- [ ] Binance sandbox conectado en `/settings`
- [ ] Bots ejecutando y semaforo de solidez visible en `/bots`
- [ ] Continuidad actualizada tras primeras 24h
