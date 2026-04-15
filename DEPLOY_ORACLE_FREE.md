# Despliegue 24/7 en Oracle Cloud Free

Guia para desplegar `TradingBotsPlatform` en una VM de Oracle Cloud y dejarlo ejecutando 24h con Docker.

## 1) Requisitos y consideraciones

- Recomendado: instancia **x86_64** (para SQL Server container).
- Si solo puedes usar ARM, este `docker-compose` con SQL Server puede no ser compatible.
- Dominio/SSL es opcional para primer arranque (puedes exponer por IP y puerto `5001`).

## 2) Crear VM en Oracle Cloud

1. Crear instancia Ubuntu 22.04.
2. Asignar IP publica.
3. Abrir puertos:
   - `22` (SSH)
   - `5001` (app)
   - opcional `1433` solo si quieres acceso externo a SQL Server (normalmente no recomendado).
4. Guardar la llave SSH.

## 3) Conectarte por SSH

```bash
ssh -i /ruta/tu_llave.pem ubuntu@TU_IP_PUBLICA
```

## 4) Instalar Docker + Compose plugin

```bash
sudo apt-get update
sudo apt-get install -y ca-certificates curl gnupg
sudo install -m 0755 -d /etc/apt/keyrings
curl -fsSL https://download.docker.com/linux/ubuntu/gpg | sudo gpg --dearmor -o /etc/apt/keyrings/docker.gpg
sudo chmod a+r /etc/apt/keyrings/docker.gpg
echo \
  "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/ubuntu \
  $(. /etc/os-release && echo "$VERSION_CODENAME") stable" | sudo tee /etc/apt/sources.list.d/docker.list > /dev/null
sudo apt-get update
sudo apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
sudo usermod -aG docker $USER
newgrp docker
```

## 5) Subir el proyecto al servidor

Opcion A (git):

```bash
git clone <TU_REPO_GIT> TradingBotsPlatform
cd TradingBotsPlatform
```

Opcion B (zip/scp): copia la carpeta del proyecto y entra al directorio.

## 6) Configurar variables

```bash
cp .env.example .env
nano .env
```

Define una password fuerte para `MSSQL_SA_PASSWORD` (min 8 chars, mayus/minus/numero/simbolo).

## 7) Levantar servicios

```bash
docker compose up -d --build
```

Verificar:

```bash
docker compose ps
docker compose logs -f tradingbots-app
```

## 8) Comprobar la app

- Swagger: `http://TU_IP_PUBLICA:5001/swagger`
- App: `http://TU_IP_PUBLICA:5001/`

## 9) Operacion y mantenimiento

- Reiniciar stack:
  ```bash
  docker compose restart
  ```
- Actualizar versión:
  ```bash
  git pull
  docker compose up -d --build
  ```
- Ver logs:
  ```bash
  docker compose logs -f
  ```

## 10) Seguridad recomendada (siguiente paso)

- Poner `Nginx` reverse proxy y `HTTPS` con Let's Encrypt.
- Restringir puerto `1433` a red interna (o cerrarlo).
- Cambiar credenciales admin por defecto en app (`appsettings` / variables).
- Configurar backup de volumen `mssql_data`.

## Nota importante sobre costo

Oracle Free puede ser gratuito permanentemente, pero recursos disponibles dependen de capacidad de la region.
Si tu instancia gratuita se recicla o no hay x86 disponible, considera:

- mantener app en Oracle y mover DB a un servicio gestionado,
- o usar un proveedor de pago muy bajo costo para estabilidad total.
