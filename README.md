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
| `GET` | `/api/forms`, `/api/forms/{id}`, `/api/forms/{id}/submissions` | autenticado |
| `POST`, `PUT`, `DELETE` | `/api/forms[/{id}]` | `Admin` |
| `PATCH`, `DELETE` | `/api/form-submissions/{id}` | autenticado / `Admin` |
| `GET`, `PATCH` / `DELETE` | `/api/messages[/{id}]` | autenticado / `Admin` |
| `GET` | `/api/recordings`, `/api/recordings/{id}/audio` | autenticado (audio con rangos) |
| `DELETE` | `/api/recordings/{id}` | `Admin` |
| `GET` / `PUT` | `/api/settings/calls` | autenticado / `Admin` |
| `GET` | `/api/knowledge`, `/api/knowledge/{id}`, `/api/knowledge/{id}/documents` | autenticado |
| `POST`, `PUT`, `DELETE` | `/api/knowledge[/{id}]` | `Admin` |
| `POST` | `/api/knowledge/{id}/documents` | `Admin`; multipart `files`, hasta 20 MB por archivo |
| `DELETE` | `/api/knowledge/documents/{id}` | `Admin` |
| `POST` | `/api/knowledge/search` | autenticado; prueba de búsqueda |
| `GET`, `POST`, `PUT`, `DELETE` | `/api/http-tools[/{id}]` | `Admin` |
| `POST` | `/api/http-tools/{id}/test` | `Admin`; ejecuta con valores de prueba |
| `GET`, `POST`, `PUT`, `DELETE` | `/api/mcp-servers[/{id}]` | `Admin`; al guardar se conecta y lee las herramientas |
| `POST` | `/api/mcp-servers/{id}/sync`, `/api/mcp-servers/{id}/tools/{tool}/test` | `Admin` |
| `GET`, `POST`, `DELETE` | `/api/mcp-tokens[/{id}]` | `Admin`; el token completo solo viaja al crearlo |
| MCP | `/api/mcp` | token MCP (`Authorization: Bearer mapache_mcp_…`) |
| `POST` | `/api/forms/generate` | `Admin`; devuelve un borrador armado por la IA, sin guardarlo |
| `GET`, `POST` | `/api/forms/{id}/actions` | `Admin` |
| `PUT`, `DELETE` | `/api/form-actions/{id}` | `Admin` |
| `POST` | `/api/form-actions/{id}/test` | `Admin` (envía una respuesta de ejemplo en el momento) |
| `POST` | `/api/action-executions/{id}/retry` | `Admin` |
| `GET` / `PUT` | `/api/settings/smtp` | `Admin` |
| `POST` | `/api/agent-tools/forms/submit` | token de tools (server tool `submit_form`) |
| `POST` | `/api/agent-tools/messages` | token de tools (server tool `leave_message`) |
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

### Formularios, recados y grabaciones

Las tools se crean en el agente de ElevenLabs como *server tools* (POST, JSON), con el mismo header `Authorization` que las demás. En los parámetros `conversation_id` y `caller_number` conviene usar las variables de sistema `system__conversation_id` y `system__caller_id`: enlazan la respuesta o el recado con la grabación.

- **`submit_form`** → `/api/agent-tools/forms/submit`. Body: `form` (nombre del formulario), `values` (objeto campo → valor), `conversation_id`, `caller_number`. Responde siempre 200: `ok: false` con `problems` si falta o no es válido algún dato, para que el bot vuelva a preguntar. Los valores se normalizan por tipo (teléfono, correo, número, sí/no, opciones).
- **`leave_message`** → `/api/agent-tools/messages`. Body: `recipient`, `message`, `caller_name`, `callback_number`, `urgent`, `conversation_id`. El destinatario se enlaza con el directorio si hay una sola coincidencia clara.
- **Contexto para el bot:** el Custom LLM agrega los formularios activos y las instrucciones de recados solo si el agente tiene la tool correspondiente (`BotContext`); pedirle usar una herramienta que no existe lo confunde.
- **Grabaciones:** con "Grabar llamadas" activo en Configuración, el webhook post-llamada guarda el MP3 de los eventos `post_call_audio` (activar *Send audio data* en el webhook de ElevenLabs). Los archivos van a `Recordings:Path` (volumen `recordings` en Docker); una conversación se guarda una sola vez aunque ElevenLabs reintente. El aviso de grabación se agrega a las instrucciones del bot; conviene incluirlo también en el primer mensaje del agente, que ElevenLabs dice antes de consultar al LLM.

### Bases de conocimiento

Se suben PDF, Word (.docx), TXT, Markdown, CSV o HTML. `DocumentText` extrae el texto (PdfPig para PDF; el .docx se lee como zip) y lo parte en fragmentos de ~900 caracteres con solapamiento; se guarda el texto, no el archivo. Cada fragmento tiene una columna `tsvector` calculada por Postgres (`to_tsvector('spanish', SearchText)`, índice GIN) sobre el texto en minúsculas y sin tildes.

En cada turno, el Custom LLM busca con el último mensaje de quien llama (palabras unidas con OR, ranking `ts_rank_cd`) en las bases activas y agrega los 3 fragmentos más relevantes como mensaje de sistema. Es texto completo, no embeddings: responde en milisegundos y funciona con cualquier proveedor, incluido Claude, que no tiene API de embeddings.

### Herramientas del bot

El Custom LLM agrega a las tools que trae ElevenLabs las de Mapache (`BotToolRegistry`; si el agente tiene una con el mismo nombre, gana la del agente):

- `search_knowledge_base`, si hay alguna base activa con contenido.
- **Peticiones HTTP** (`HttpTool`): método, URL y cuerpo con `{{parametro}}` (en la URL se codifica; en el cuerpo se inserta como JSON), headers cifrados, parámetros tipados y timeout. La respuesta se compacta y se recorta a 4000 caracteres.
- **Servidores MCP externos** (`McpServerConnection`, SDK oficial `ModelContextProtocol`): se leen sus herramientas al guardar o con "Volver a leer", y cada una se habilita por separado. El modelo las ve como `mcp_<servidor>_<herramienta>`.

`SseToolInterceptor` se pone entre el proveedor y ElevenLabs: el texto sale en el momento (el "déjame revisar" se escucha mientras tanto) y las llamadas a herramientas, el `finish_reason` y el `[DONE]` se retienen. Si alguna es de Mapache, se ejecuta, se agrega el resultado y se vuelve a consultar al modelo (hasta 4 rondas); si todas son del agente, lo retenido se entrega tal cual.

### Servidor MCP de Mapache

`/api/mcp` (Streamable HTTP, sin estado) expone `search_knowledge_base`, `search_directory`, `list_messages`, `update_message_status`, `list_forms` y `list_form_submissions`. Se autentica con tokens creados en Configuración (se guarda el SHA-256; el valor se muestra una vez). nginx lo sirve sin buffer. Ejemplo con Claude Code:

```bash
claude mcp add --transport http mapache https://<host>/api/mcp --header "Authorization: Bearer mapache_mcp_..."
```

### Formularios con IA

`POST /api/forms/generate` con `{"description": "..."}` le pide al proveedor activo (Configuración → IA) un formulario en JSON estructurado: `response_format: json_schema` estricto en OpenAI y Gemini, `output_config.format` con el SDK oficial en Claude (`LlmJsonClient`). `FormGenerator.Sanitize` deja el borrador válido aunque el modelo se desvíe: claves en snake_case únicas, tipos conocidos, opciones solo en `Choice` y como máximo 25 campos. El panel lo carga en el editor para revisarlo antes de guardar.

### Acciones de formularios

Cada respuesta nueva crea una `ActionExecution` por acción activa del formulario y la encola en Hangfire (`FormActionJob`). Si el destino falla, Hangfire reintenta 5 veces (30 s, 2 min, 10 min, 30 min, 1 h); cada intento actualiza estado, intentos y último error, que el panel muestra en la respuesta con opción de reintentar.

| Tipo | Destino | Qué se envía |
|------|---------|--------------|
| Webhook | URL propia | `POST` JSON con `event: "form.submitted"`, formulario y respuesta (`values` por clave y `fields` con etiquetas). Con secreto: `X-Mapache-Signature: sha256=<HMAC-SHA256 del cuerpo>` |
| Teams | URL de un flujo "Publicar en un canal cuando se reciba una solicitud de webhook" | Tarjeta adaptable con los datos |
| Google Chat | Webhook de un espacio | Texto con los datos |
| Slack | Incoming webhook | Texto con los datos |
| Correo | Destinatarios separados por coma | HTML y texto por el SMTP de Configuración (MailKit; STARTTLS en 587, TLS en 465) |

Los secretos de webhook y la contraseña SMTP se guardan cifrados con `ISecretProtector`.

### Tiempo real y trabajos en segundo plano

- **SignalR** (`Realtime/EventsHub.cs`): avisa al panel cuando cambian las extensiones. En Fase 2 suma el estado del registro SIP y las llamadas en vivo.
- **Hangfire**: guarda sus datos en Postgres (schema `hangfire`) y corre su servidor dentro de la API. Ejecuta las acciones de formularios con reintentos.
- **Serilog**: logs legibles en desarrollo y JSON compacto fuera de él, con una línea por request.

## Seguridad

- **Secretos:** nunca en `appsettings*.json`. En desarrollo, user-secrets; en Docker/servidores, variables de entorno (`.env`, fuera de git).
- **Contraseñas SIP:** cifradas con ASP.NET Data Protection (`ISecretProtector`); la API nunca las devuelve. Las llaves de Data Protection viven en la tabla `DataProtectionKeys`: si se pierden, las contraseñas guardadas quedan ilegibles. Hoy esas llaves no están cifradas en reposo (pendiente para Fase 8: certificado o KMS).
- **Usuarios:** contraseñas con `PasswordHasher` de ASP.NET Identity.

## Motor SIP

`SipEngine` (en `src/Mapache.Sip/Engine/`) corre dentro de la API y mantiene registrada cada extensión habilitada. Lee las cuentas de la base al arrancar, cuando se edita una extensión (`RequestReload`) y cada minuto; una cuenta cuya configuración cambió se des-registra y se vuelve a crear.

| Pieza | Qué hace |
|-------|----------|
| `SipAccountRuntime` | Una cuenta: su transporte (UDP, TCP o TLS en el puerto local elegido), el REGISTER con proxy, dominio y usuario de autenticación, el paso al servidor secundario tras fallar, el keepalive de NAT (doble CRLF) y las llamadas entrantes |
| `CallBridge` (`Media/`) | Puentea RTP ↔ WebSocket de ElevenLabs: jitter buffer, frames de 20 ms, barge-in, DTMF y REFER |
| `ElevenLabsConversation` | Cliente del WebSocket; manda `custom_llm_extra_body.extension_id` para el Custom LLM |
| `AudioConverter`, `JitterBuffer`, `LatencyTracker` | Audio G.711, orden de paquetes y latencia turno a turno |

La API le da al motor las cuentas (con la contraseña descifrada), el agente de ElevenLabs (Configuración → Voz) y recibe los eventos (`Sip/SipBindings.cs`): guarda cada llamada con su dirección, estado, motivo de fin y transcripción, y avisa al panel por SignalR (`ExtensionStatusChanged`, `CallsChanged`, `CallTranscript`).

Llamadas entrantes: modo bot o bot con paso a persona → contesta el agente de ElevenLabs; modo persona → se rechaza con 480 hasta que exista el softphone del panel. Con el límite de simultáneas alcanzado responde 486. Sin agente configurado responde 503 y la llamada queda como fallida con el motivo.

Qué opciones de la cuenta se aplican hoy: servidores principal y secundario, proxy, dominio, login, contraseña, nombre para mostrar, transporte, puerto local, IP pública o STUN, expiración, reintento, keepalive, códecs (PCMU/PCMA para el bot), simultáneas y quién contesta. Quedan guardadas pero sin efecto todavía: SRTP, ICE, refresco de sesión, DTMF por SIP INFO o en banda, buzón, prefijo y número oculto (estos tres son de llamadas salientes).

`Sip:Enabled=false` apaga el motor (desarrollo sin red SIP). `Sip:RtpPortStart`/`RtpPortEnd` fijan el rango de puertos de audio para poder publicarlos en Docker.

### Qué mirar en los logs

- `Registrando <usuario>@<servidor>` y el estado en el panel; un `401 Unauthorized` es usuario o contraseña incorrectos.
- `Bridge iniciado`: códec negociado, formatos de ElevenLabs y si hay passthrough (**D3**). Para passthrough en ambos sentidos, configurar el agente con entrada y salida `μ-law 8000 Hz`.
- `Latencia turno`: una línea por turno; al colgar, el resumen con p50/p90/máx. La meta es menos de ~1,5 s.

## Red

El motor SIP necesita que los servidores SIP puedan llegarle: IP pública (o NAT resuelto con IP pública o STUN y keepalive) y los puertos UDP de cada cuenta y del audio. En Docker sobre Linux lo más simple es `network_mode: host` para la API; con Docker Desktop (Windows/Mac) el NAT del contenedor sirve para registrar y recibir llamadas mientras el keepalive mantenga abierto el mapeo, pero no para producción. Ver decisión D4 en el plan.
