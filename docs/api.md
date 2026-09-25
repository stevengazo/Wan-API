# 🔗 Referencia de la API

Base: `/api`. JSON en camelCase, enums como texto. En Development el documento OpenAPI está en `/openapi/v1.json`.

## Autenticación

Mapache tiene cuatro esquemas, separados a propósito: un token de un tipo no sirve en el lugar de otro.

| Esquema | Quién lo usa | Header |
|---------|--------------|--------|
| 🧑 **JWT** | El panel (usuarios) | `Authorization: Bearer <jwt>` |
| 🗣️ **Token de tools del agente** | ElevenLabs (Custom LLM y server tools) | `Authorization: Bearer <AgentWebhooks:ToolToken>` |
| ✍️ **Firma HMAC** | Webhook post-llamada de ElevenLabs | `ElevenLabs-Signature: t=…,v0=…` |
| 🤖 **Token MCP** | Clientes MCP | `Authorization: Bearer mapache_mcp_…` |

**Roles:** `Admin` configura todo; `Operator` ve el panel y atiende recados, respuestas y grabaciones.

**Errores:** [ProblemDetails](https://www.rfc-editor.org/rfc/rfc9457) con mensajes en español; los de validación traen `errors` por campo.

```json
{
  "title": "Revisa los datos",
  "status": 400,
  "errors": { "sipServer": ["El servidor SIP es obligatorio."] }
}
```

En la columna *Acceso*: **anónimo**, **usuario** (cualquier rol), **admin**, **agente** (token de tools), **firma** o **MCP**.

## Sesión y perfil

| Método | Ruta | Acceso | Descripción |
|--------|------|--------|-------------|
| `POST` | `/auth/login` | anónimo | Devuelve `{accessToken, expiresAt, user}`. 10 intentos por minuto por IP. |
| `POST` | `/auth/register` | anónimo | Crea un usuario `Admin` activo (mismo límite). Ciérralo o restríngelo antes de exponer el panel. |
| `GET` / `PUT` | `/auth/me` | usuario | Perfil; el `PUT` cambia el nombre |
| `POST` | `/auth/password` | usuario | Cambia la contraseña; pide la actual |

## Usuarios

| Método | Ruta | Acceso | Descripción |
|--------|------|--------|-------------|
| `GET` | `/users` | admin | Lista, con `isCurrent` para el usuario de la sesión |
| `POST` | `/users` | admin | Alta con nombre, correo, contraseña inicial y rol |
| `PUT` | `/users/{id}` | admin | Cambia nombre y rol |
| `POST` | `/users/{id}/password` | admin | Pone una contraseña nueva |
| `DELETE` | `/users/{id}` | admin | Baja |

Responde `409` si intentas borrarte a ti mismo o dejar el sistema sin administradores.

## Extensiones y llamadas

| Método | Ruta | Acceso | Descripción |
|--------|------|--------|-------------|
| `GET` | `/extensions`, `/extensions/{id}` | usuario | Cuentas SIP (nunca la contraseña) |
| `POST`, `PUT`, `DELETE` | `/extensions[/{id}]` | admin | El motor SIP se recarga solo |
| `GET` | `/extensions/status` | usuario | Estado del registro y llamadas activas de cada cuenta |
| `GET` | `/calls`, `/calls/{id}` | usuario | Historial y detalle con transcripción |
| `GET` / `PUT` | `/settings/voice` | usuario / admin | API key (cifrada) y agente de ElevenLabs |
| `GET` / `PUT` | `/settings/calls` | usuario / admin | Grabación y aviso |

## IA

| Método | Ruta | Acceso | Descripción |
|--------|------|--------|-------------|
| `GET` | `/llm/settings` | usuario | Proveedor activo, modelos, instrucciones y si cada API key está cargada |
| `PUT` | `/llm/settings` | admin | Guarda; una key vacía conserva la anterior |
| `POST` | `/llm/v1/chat/completions` | agente | Custom LLM de ElevenLabs (SSE) |

## Directorio

| Método | Ruta | Acceso |
|--------|------|--------|
| `GET` | `/directory`, `/directory/{id}` | usuario |
| `POST`, `PUT`, `DELETE` | `/directory[/{id}]` | admin |
| `POST` | `/agent-tools/directory/search` | agente |

## Formularios

| Método | Ruta | Acceso | Descripción |
|--------|------|--------|-------------|
| `GET` | `/forms`, `/forms/{id}`, `/forms/{id}/submissions` | usuario | |
| `POST`, `PUT`, `DELETE` | `/forms[/{id}]` | admin | |
| `POST` | `/forms/generate` | admin | Borrador con IA; no se guarda |
| `PATCH` / `DELETE` | `/form-submissions/{id}` | usuario / admin | Estado de una respuesta |
| `GET`, `POST` | `/forms/{id}/actions` | admin | Acciones del formulario |
| `PUT`, `DELETE` | `/form-actions/{id}` | admin | |
| `POST` | `/form-actions/{id}/test` | admin | Envía una respuesta de ejemplo |
| `POST` | `/action-executions/{id}/retry` | admin | Reintenta una ejecución fallida |
| `GET` / `PUT` | `/settings/smtp` | admin | Servidor de correo |
| `POST` | `/agent-tools/forms/submit` | agente | Server tool `submit_form` |

## Recados y grabaciones

| Método | Ruta | Acceso | Descripción |
|--------|------|--------|-------------|
| `GET` | `/messages` | usuario | |
| `PATCH` / `DELETE` | `/messages/{id}` | usuario / admin | Estado: `New`, `Read`, `Done` |
| `POST` | `/agent-tools/messages` | agente | Server tool `leave_message` |
| `GET` | `/recordings` | usuario | |
| `GET` | `/recordings/{id}/audio` | usuario | MP3 con soporte de rangos |
| `DELETE` | `/recordings/{id}` | admin | |

## Conocimiento

| Método | Ruta | Acceso | Descripción |
|--------|------|--------|-------------|
| `GET` | `/knowledge`, `/knowledge/{id}`, `/knowledge/{id}/documents` | usuario | |
| `POST`, `PUT`, `DELETE` | `/knowledge[/{id}]` | admin | |
| `POST` | `/knowledge/{id}/documents` | admin | Multipart `files`, hasta 20 MB por archivo |
| `DELETE` | `/knowledge/documents/{id}` | admin | |
| `POST` | `/knowledge/search` | usuario | Prueba de búsqueda |

## Herramientas

| Método | Ruta | Acceso | Descripción |
|--------|------|--------|-------------|
| `GET`, `POST`, `PUT`, `DELETE` | `/http-tools[/{id}]` | admin | Peticiones HTTP |
| `POST` | `/http-tools/{id}/test` | admin | Ejecuta con valores de prueba |
| `GET`, `POST`, `PUT`, `DELETE` | `/mcp-servers[/{id}]` | admin | Al guardar se conecta y lee las herramientas |
| `POST` | `/mcp-servers/{id}/sync` | admin | Vuelve a leer las herramientas |
| `POST` | `/mcp-servers/{id}/tools/{tool}/test` | admin | Prueba una herramienta |

## Servidor MCP

| Método | Ruta | Acceso | Descripción |
|--------|------|--------|-------------|
| `GET` | `/mcp-tokens` | admin | Tokens (sin el valor) |
| `GET` | `/mcp-tokens/tools` | admin | Herramientas que expone el servidor |
| `POST` | `/mcp-tokens` | admin | `{name, allowAll, allowedTools}`; el valor solo viaja en esta respuesta |
| `PUT` | `/mcp-tokens/{id}` | admin | Cambia los permisos |
| `DELETE` | `/mcp-tokens/{id}` | admin | Revoca |
| MCP | `/mcp` | MCP | Servidor Streamable HTTP ([servidor-mcp.md](servidor-mcp.md)) |

## Agente y webhooks

| Método | Ruta | Acceso | Descripción |
|--------|------|--------|-------------|
| `POST` | `/agent-tools/ping` | agente | Prueba del token |
| `POST` | `/webhooks/elevenlabs/post-call` | firma | Transcripción y audio al terminar |

## Otros

| Ruta | Acceso | Descripción |
|------|--------|-------------|
| `GET /health` | anónimo | Chequeo de vida |
| `WS /hubs/events` | usuario (JWT en `access_token` del query string) | SignalR |
| `/hangfire` | solo Development y desde localhost | Panel de trabajos |

## Eventos en tiempo real (SignalR)

Hub `/hubs/events`. El panel escucha:

| Evento | Carga | Cuándo |
|--------|-------|--------|
| `ExtensionsChanged` | | Se creó, editó o borró una extensión |
| `ExtensionStatusChanged` | `{extensionId, state, server, error, activeCalls, since}` | Cambió el registro SIP |
| `CallsChanged` | | Empezó, se contestó o terminó una llamada |
| `CallTranscript` | `callId, speaker, text` | Nueva línea de transcripción en vivo |
| `MessagesChanged` | | Nuevo recado |
| `FormSubmissionsChanged` | | Nueva respuesta de formulario |
| `RecordingsChanged` | | Nueva grabación |

## Ejemplo con curl

```bash
TOKEN=$(curl -s -X POST http://localhost:8080/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"admin@mapache.local","password":"..."}' | jq -r .accessToken)

curl -s http://localhost:8080/api/extensions/status -H "Authorization: Bearer $TOKEN" | jq
```
