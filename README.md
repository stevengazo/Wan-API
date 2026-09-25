# Mapache — Backend

Solución .NET 10 del bot telefónico: motor SIP, bridge de audio a ElevenLabs Conversational AI y API para el panel. Ver [PLAN.md](../PLAN.md) para fases y decisiones abiertas.

## Requisitos

- [.NET SDK 10](https://dotnet.microsoft.com/download)
- Docker (Postgres + pgvector)

## Estructura

| Proyecto | Responsabilidad |
|----------|-----------------|
| `src/Mapache.Api` | ASP.NET Core: controllers, SignalR, webhook de tools del agente, Hangfire |
| `src/Mapache.Domain` | Entidades y contratos (`Extension` = cuenta SIP genérica, `ITicketingConnector`, ...). Sin dependencias externas |
| `src/Mapache.Infrastructure` | EF Core + Postgres/pgvector, motor de acciones, integraciones |
| `src/Mapache.Sip` | Motor SIP (SIPSorcery) y bridge RTP ↔ WebSocket de ElevenLabs |
| `tests/Mapache.Tests` | Pruebas (xUnit) |

Dependencias: `Api` → `Domain`, `Infrastructure`, `Sip`; `Infrastructure` y `Sip` → `Domain`.

## Correr en local

Desde la raíz del repo: `cp .env.example .env`, completar los valores y levantar Postgres:

```bash
docker compose up -d db
```

Secretos de la API en user-secrets (la contraseña de la base es la de `POSTGRES_PASSWORD` en `.env`):

```bash
cd backend
dotnet user-secrets set "ConnectionStrings:Mapache" "Host=localhost;Port=5432;Database=mapache;Username=mapache;Password=<POSTGRES_PASSWORD>" --project src/Mapache.Api
dotnet user-secrets set "Auth:Jwt:SigningKey" "$(openssl rand -base64 48)" --project src/Mapache.Api
dotnet user-secrets set "Auth:BootstrapAdmin:Email" "admin@mapache.local" --project src/Mapache.Api
dotnet user-secrets set "Auth:BootstrapAdmin:Password" "<contraseña>" --project src/Mapache.Api
dotnet user-secrets set "AgentWebhooks:ToolToken" "$(openssl rand -hex 32)" --project src/Mapache.Api
dotnet user-secrets set "AgentWebhooks:PostCallSecret" "<secreto del webhook en ElevenLabs>" --project src/Mapache.Api
```

```bash
dotnet build
dotnet test
dotnet run --project src/Mapache.Api
```

La API queda en `http://localhost:5238`. Al arrancar aplica las migraciones pendientes y, si la base no tiene usuarios, el admin de `Auth:BootstrapAdmin`. En Development el documento OpenAPI está en `/openapi/v1.json`; [Mapache.Api.http](src/Mapache.Api/Mapache.Api.http) tiene requests de ejemplo.

Para correr todo en Docker ver el [README de la raíz](../README.md). En Docker la API toma sus secretos de `backend/.env` (plantilla: [.env.example](.env.example)); `dotnet run` local usa user-secrets.

## Configuración

| Archivo | Uso |
|---------|-----|
| `appsettings.json` | Todas las claves, con valores por defecto no secretos y los secretos vacíos como documentación |
| `appsettings.Development.json` | Logs `Debug` para `Mapache.*` |
| `appsettings.Production.json` | `ReverseProxy:Enabled=true` (la API corre detrás de nginx) |
| user-secrets | Secretos para `dotnet run` local |
| `backend/.env` | Secretos para el contenedor (lo carga `docker-compose.yml`) |

`ReverseProxy:Enabled` hace que la API tome la IP real del cliente de `X-Forwarded-For`, lo que importa para el límite de intentos de login. Solo activarlo si hay un proxy delante: sin proxy, el cliente podría falsear su IP.

## Migraciones

```bash
dotnet ef migrations add <Nombre> --project src/Mapache.Infrastructure --startup-project src/Mapache.Api --output-dir Persistence/Migrations
```

## API

| Método | Ruta | Rol |
|--------|------|-----|
| `POST` | `/api/auth/login` | anónimo (10 intentos/min por IP) |
| `POST` | `/api/auth/register` | anónimo (mismo límite que el login); crea un `Admin` activo (cerrar o restringir antes de exponer el panel) |
| `GET`, `PUT` | `/api/auth/me` | autenticado (el `PUT` cambia el nombre) |
| `POST` | `/api/auth/password` | autenticado; pide la contraseña actual |
| `GET` | `/api/extensions`, `/api/extensions/{id}` | autenticado |
| `POST`, `PUT`, `DELETE` | `/api/extensions[/{id}]` | `Admin` |
| `GET` | `/health` | anónimo |
| `POST` | `/api/agent-tools/ping` | token de tools del agente |
| `POST` | `/api/llm/v1/chat/completions` | token de tools del agente (Custom LLM de ElevenLabs) |
| `GET` | `/api/directory`, `/api/directory/{id}` | autenticado |
| `POST`, `PUT`, `DELETE` | `/api/directory[/{id}]` | `Admin` |
| `POST` | `/api/agent-tools/directory/search` | token de tools del agente (server tool `search_directory`) |
| `GET` | `/api/llm/settings` | autenticado (sin API keys, solo si están cargadas) |
| `PUT` | `/api/llm/settings` | `Admin` |
| `POST` | `/api/webhooks/elevenlabs/post-call` | firma HMAC de ElevenLabs |
| WS | `/hubs/events` (SignalR) | autenticado (JWT por `access_token` en query string) |
| `GET` | `/hangfire` | solo en Development y desde localhost |

JWT Bearer con roles `Admin` y `Operator`. Errores en formato ProblemDetails con mensajes en español.

### Webhooks del agente (ElevenLabs)

- **Server tools:** en cada tool del agente, agregar el header `Authorization` de tipo *Secret* con el valor `Bearer <AgentWebhooks:ToolToken>`. `POST /api/agent-tools/ping` sirve para probar la configuración. Es un esquema aparte del JWT de usuarios: un token no sirve en el lugar del otro.
- **Webhook post-llamada:** apuntarlo a `/api/webhooks/elevenlabs/post-call` y guardar el secreto que genera ElevenLabs en `AgentWebhooks:PostCallSecret`. Se valida el header `ElevenLabs-Signature` (HMAC-SHA256 de `"{t}.{body}"`) y se rechazan firmas de más de 30 minutos. Por ahora solo registra la recepción; la persistencia es de Fase 2.

### LLM del bot (Custom LLM)

ElevenLabs sigue haciendo la voz (reconocimiento, síntesis, turnos e interrupciones); las respuestas las genera Mapache con OpenAI, Gemini o Claude.

- **En el agente de ElevenLabs:** LLM → *Custom LLM*, URL `https://<host>/api/llm/v1` (ElevenLabs agrega `/chat/completions`) y como API key un secreto con el mismo valor que `AgentWebhooks:ToolToken`. Activar el envío de *extra body*.
- **Configuración general** (igual para todas las cuentas), desde el panel en Configuración: proveedor activo, modelo e API key de cada proveedor e instrucciones del bot. Se guarda en `LlmSettings` / `LlmProviderSettings`; las API keys van cifradas con `ISecretProtector` y la API nunca las devuelve. Modelo vacío = `Llm:{Proveedor}:DefaultModel` de `appsettings.json`; instrucciones vacías = las del agente.
- **OpenAI y Gemini** hablan el formato de chat completions: la petición se reenvía con el modelo y las instrucciones configuradas y la respuesta se copia tal cual (Gemini por su [API compatible con OpenAI](https://ai.google.dev/gemini-api/docs/openai)).
- **Claude** va por la Messages API con el SDK oficial (`AnthropicChatClient`): se traducen mensajes, tools y resultados, y el streaming se reescribe al formato SSE de OpenAI. Usa esfuerzo bajo para responder rápido y, en `claude-opus-5` / `claude-fable-5-1`, el fallback del servidor ante rechazos (`fallbacks: "default"`); si igual declina, el bot dice una frase de cortesía.

### Directorio de transferencias

Destinos a los que el bot puede transferir (nombre, área, extensión/número/URI SIP y cuándo transferir), administrados en el panel.

- **Siempre presente:** el Custom LLM agrega las entradas activas como un mensaje de sistema, después de las instrucciones. El bot las conoce sin configurar nada más.
- **Server tool opcional:** `search_directory` en el agente, `POST /api/agent-tools/directory/search` con body `{"query": "..."}` y el mismo header `Authorization` de los demás tools. Busca sin mayúsculas ni tildes por nombre, área, destino y descripción.
- La transferencia en sí (REFER) la hace el motor SIP (Fase 3); por ahora el bot sabe a dónde transferir.

### Tiempo real y trabajos en segundo plano

- **SignalR** (`Realtime/EventsHub.cs`): avisa al panel cuando cambian las extensiones. En Fase 2 suma el estado del registro SIP y las llamadas en vivo.
- **Hangfire**: guarda sus datos en Postgres (schema `hangfire`) y corre su servidor dentro de la API. Todavía no hay jobs; el motor de acciones de Fase 4 va sobre esto.
- **Serilog**: logs legibles en desarrollo y JSON compacto fuera de él, con una línea por request.

## Seguridad

- **Secretos:** nunca en `appsettings*.json`. En desarrollo, user-secrets; en Docker/servidores, variables de entorno (`.env`, fuera de git).
- **Contraseñas SIP:** cifradas con ASP.NET Data Protection (`ISecretProtector`); la API nunca las devuelve. Las llaves de Data Protection viven en la tabla `DataProtectionKeys`: si se pierden, las contraseñas guardadas quedan ilegibles. Hoy esas llaves no están cifradas en reposo (pendiente para Fase 8: certificado o KMS).
- **Usuarios:** contraseñas con `PasswordHasher` de ASP.NET Identity.

## POC Fase 0: bridge SIP ↔ ElevenLabs

Código en `src/Mapache.Sip/Poc/`. Corre como `BackgroundService` dentro de la API y solo se activa con `Sip:Poc:Enabled=true`.

| Clase | Qué hace |
|-------|----------|
| `SipPocWorker` | Registra la extensión, atiende INVITEs y abre una conversación de ElevenLabs por llamada mientras suena |
| `CallBridge` | Puentea RTP ↔ WebSocket: uplink ordenado por jitter buffer; downlink en frames de 20 ms con silencio de relleno (mantiene el NAT); barge-in; DTMF; REFER |
| `ElevenLabsConversation` | Cliente del WebSocket (signed URL, metadata de formatos, audio, interrupción, ping/pong) |
| `AudioConverter` | G.711 ↔ formato de ElevenLabs. Con PCMU + `ulaw_8000` el audio pasa sin transcodificar |
| `JitterBuffer` | Reordena paquetes RTP por secuencia y descarta los tardíos |
| `LatencyTracker` | Mide fin de voz del llamante → primer audio del agente y resume p50/p90 al colgar |

### Configurar

```bash
dotnet user-secrets set "Sip:Poc:Enabled" "true" --project src/Mapache.Api
dotnet user-secrets set "Sip:Poc:Sip:Server" "<servidor SIP>" --project src/Mapache.Api
dotnet user-secrets set "Sip:Poc:Sip:Username" "<extensión>" --project src/Mapache.Api
dotnet user-secrets set "Sip:Poc:Sip:Password" "<contraseña>" --project src/Mapache.Api
dotnet user-secrets set "Sip:Poc:ElevenLabs:ApiKey" "<api key>" --project src/Mapache.Api
dotnet user-secrets set "Sip:Poc:ElevenLabs:AgentId" "<agent id>" --project src/Mapache.Api

# Opcionales
dotnet user-secrets set "Sip:Poc:Sip:PublicIpAddress" "<ip pública>" --project src/Mapache.Api   # detrás de NAT
dotnet user-secrets set "Sip:Poc:TransferTarget" "<extensión o sip:uri>" --project src/Mapache.Api # prueba REFER
```

Los valores no secretos por defecto (`ListenPort` 5060, `ExpirySeconds` 120, `TransferDtmfDigit` 9) están en `appsettings.json`. El agente recibe la variable dinámica `caller_id`.

### Qué mirar en los logs

- `Bridge iniciado`: códec negociado, formatos de ElevenLabs y si hay passthrough (**D3**). Para passthrough en ambos sentidos, configurar el agente con entrada y salida `μ-law 8000 Hz`.
- `Latencia turno`: una línea por turno; al colgar, el resumen con p50/p90/máx. La meta es menos de ~1,5 s.
- `REFER aceptado` o `rechazado`: al marcar `9` con `TransferTarget` configurado (**D2**).
- Las demás teclas se envían al agente como `contextual_update`.

## Red

El motor SIP necesita IP pública (o NAT resuelto) y puertos UDP abiertos: 5060 para SIP y un rango efímero para RTP (lo elige el sistema operativo). Ver decisión D4 en el plan.
