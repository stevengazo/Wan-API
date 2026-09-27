# Wan API

API de **Wan**, bot telefónico con IA: motor SIP en .NET 10 + ElevenLabs Conversational AI. Incluye la API REST, el motor SIP/RTP, los trabajos en segundo plano (Hangfire), el servidor MCP y la documentación de la API con Scalar.

Se usa junto con [`wan-web`](https://github.com/stevengazo/Wan-Web) (el panel) y PostgreSQL con pgvector.

- Código: https://github.com/stevengazo/Wan-API
- Arquitecturas: `linux/amd64`, `linux/arm64`
- Corre como usuario sin privilegios (`app`)

## Tags

| Tag | Contenido |
|-----|-----------|
| `latest`, `1`, `1.2`, `1.2.3` | Versiones publicadas (tags `v*.*.*`) |
| `main` | Último commit de `main` |
| `sha-<commit>` | Un commit concreto |

## Uso rápido

```yaml
services:
  db:
    image: pgvector/pgvector:pg17
    environment:
      POSTGRES_DB: wan
      POSTGRES_USER: wan
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:?falta POSTGRES_PASSWORD}
    volumes:
      - db-data:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U wan -d wan"]
      interval: 5s
      retries: 10
    restart: unless-stopped

  api:   # el nombre importa: el nginx de wan-web hace proxy a http://api:8080
    image: ghcr.io/stevengazo/wan-api:latest
    env_file: ./backend.env
    environment:
      ConnectionStrings__Wan: Host=db;Database=wan;Username=wan;Password=${POSTGRES_PASSWORD}
    volumes:
      - recordings:/app/data/recordings
    depends_on:
      db:
        condition: service_healthy
    restart: unless-stopped

  web:
    image: ghcr.io/stevengazo/wan-web:latest
    ports:
      - "4400:80"
    depends_on:
      - api
    restart: unless-stopped

volumes:
  db-data:
  recordings:
```

Panel en `http://<host>:4400` y documentación de la API en `http://<host>:4400/api/docs`.

## Variables de entorno

Formato .NET: `__` separa secciones.

| Variable | Obligatoria | Descripción |
|----------|:-----------:|-------------|
| `ConnectionStrings__Wan` | ✅ | Cadena de conexión a PostgreSQL. |
| `Auth__Jwt__SigningKey` | ✅ | Clave de firma de los JWT, mínimo 32 caracteres (`openssl rand -base64 48`). |
| `DataProtection__KeyEncryptionKey` | ✅ | Clave maestra que cifra las llaves de Data Protection (`openssl rand -base64 32`). Guárdala aparte: sin ella no se pueden leer las credenciales SIP ni las grabaciones cifradas. |
| `Auth__BootstrapAdmin__Email` | | Admin que se crea en el primer arranque si no hay usuarios. |
| `Auth__BootstrapAdmin__Password` | | Contraseña de ese admin. |
| `AgentWebhooks__ToolToken` | | Bearer token de los server tools del agente de ElevenLabs (`openssl rand -hex 32`). |
| `AgentWebhooks__PostCallSecret` | | Secreto HMAC del webhook post-llamada de ElevenLabs. |
| `App__PublicUrl` | | URL pública del panel (la usan ElevenLabs y los clientes MCP). |
| `Sip__Enabled` | | `false` para arrancar sin registrar cuentas SIP. Por defecto `true`. |
| `ApiDocs__Enabled` | | Publica OpenAPI y Scalar en `/api/docs`. Por defecto `true` en la imagen. |

Las migraciones de la base se aplican solas al arrancar.

## Puertos y volúmenes

| | |
|---|---|
| `8080/tcp` | HTTP (API, SignalR, MCP). No hace falta publicarlo si va detrás de `wan-web`. |
| SIP / RTP (UDP) | El puerto SIP de cada extensión y el rango RTP (`Sip__RtpPortStart`, `Sip__RtpPortEnd`). En Linux lo más simple es `network_mode: host`. |
| `/app/data/recordings` | Grabaciones de llamadas. |

## Producción

- Pon un proxy con TLS delante de `wan-web`: ElevenLabs solo llama a URLs `https`.
- Respalda la base (incluye las llaves de Data Protection) y el volumen de grabaciones.
- Actualizar: `docker compose pull && docker compose up -d`.

Licencia MIT.
