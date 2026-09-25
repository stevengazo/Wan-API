# ⚙️ Configuración

Mapache se configura en dos lugares:

- **Infraestructura** (base, JWT, tokens, red): `appsettings.json`, user-secrets o variables de entorno. Se leen al arrancar.
- **Funcionalidad** (proveedor de IA, agente de voz, extensiones, SMTP, grabación): desde el panel. Se guarda en la base y aplica al instante.

## Fuentes de configuración

| Fuente | Cuándo se usa |
|--------|---------------|
| `appsettings.json` | Valores por defecto no secretos. Los secretos aparecen vacíos, como documentación. |
| `appsettings.Development.json` | Logs `Debug` para `Mapache.*`. |
| `appsettings.Production.json` | `ReverseProxy:Enabled=true`, porque en Docker la API corre detrás de nginx. |
| user-secrets | Secretos para `dotnet run` en local. |
| Variables de entorno (`backend/.env`) | Secretos del contenedor. El separador de secciones es `__`. |

## Claves

### Base de datos

| Clave | Por defecto | Descripción |
|-------|-------------|-------------|
| `ConnectionStrings:Mapache` | (requerida) | Cadena de Npgsql. En Docker la arma `docker-compose.yml` con `POSTGRES_PASSWORD`. |

### Autenticación

| Clave | Por defecto | Descripción |
|-------|-------------|-------------|
| `Auth:Jwt:SigningKey` | (requerida) | Clave HMAC de los JWT; al menos 32 bytes. `openssl rand -base64 48`. |
| `Auth:Jwt:Issuer` | `mapache` | |
| `Auth:Jwt:Audience` | `mapache-panel` | |
| `Auth:Jwt:ExpiryMinutes` | `480` | Duración de la sesión del panel. |
| `Auth:BootstrapAdmin:Email` | vacío | Admin que se crea en el primer arranque si no hay usuarios. |
| `Auth:BootstrapAdmin:Password` | vacío | |

### Cifrado de llaves

| Clave | Descripción |
|-------|-------------|
| `DataProtection:KeyEncryptionKey` | Clave maestra (base64, 32 bytes) que cifra las llaves de Data Protection. `openssl rand -base64 32`. Ver [seguridad](seguridad.md#llaves-de-data-protection). |
| `DataProtection:CertificatePath` | Alternativa: ruta a un certificado `.pfx`. |
| `DataProtection:CertificatePassword` | Contraseña del `.pfx`. |

### Agente de ElevenLabs

| Clave | Descripción |
|-------|-------------|
| `AgentWebhooks:ToolToken` | Bearer token de las server tools y API key del Custom LLM. `openssl rand -hex 32`. |
| `AgentWebhooks:PostCallSecret` | Secreto HMAC del webhook post-llamada; lo genera ElevenLabs. |

### Proveedores de IA

Las API keys y el modelo se cargan en el panel. Aquí solo van los valores por defecto:

| Clave | Por defecto |
|-------|-------------|
| `Llm:OpenAi:DefaultModel` | `gpt-4.1-mini` |
| `Llm:OpenAi:BaseUrl` | `https://api.openai.com/v1` |
| `Llm:Gemini:DefaultModel` | `gemini-2.5-flash` |
| `Llm:Gemini:BaseUrl` | `https://generativelanguage.googleapis.com/v1beta/openai` |
| `Llm:Anthropic:DefaultModel` | `claude-opus-5` |

`BaseUrl` permite apuntar a un proxy o a un servicio compatible con la API de OpenAI.

### Motor SIP

| Clave | Por defecto | Descripción |
|-------|-------------|-------------|
| `Sip:Enabled` | `true` | `false` apaga el motor, por ejemplo para desarrollar sin red SIP. |
| `Sip:RtpPortStart` | `0` | Primer puerto de audio. `0` = cualquiera libre. |
| `Sip:RtpPortEnd` | `0` | Último puerto de audio. Fíjalos para publicarlos en Docker o en el firewall. |

### Sitio

| Clave | Por defecto | Descripción |
|-------|-------------|-------------|
| `App:PublicUrl` | vacío | URL con la que se llega a Mapache desde internet (`https://mapache.miempresa.com`). Se muestra en las URLs para ElevenLabs y los clientes MCP. La de Configuración → General tiene prioridad. |

### Otros

| Clave | Por defecto | Descripción |
|-------|-------------|-------------|
| `Recordings:Path` | `data/recordings` | Carpeta de las grabaciones (volumen `recordings` en Docker). |
| `ReverseProxy:Enabled` | `false` | Toma la IP real de `X-Forwarded-For`. Solo con un proxy delante; sin proxy, el cliente podría falsear su IP y saltarse el límite de intentos de login. |
| `Serilog:*` | | Niveles de log. |

## Configuración desde el panel

| Pantalla | Qué se configura | Dónde se guarda |
|----------|------------------|-----------------|
| Configuración → General | URL pública (si está vacía, `App:PublicUrl`) | `SiteSettings` |
| Configuración → IA | Proveedor activo, API key y modelo de cada proveedor, instrucciones del bot | `LlmSettings`, `LlmProviderSettings` (keys cifradas) |
| Configuración → ElevenLabs | API key y ID del agente | `VoiceSettings` (key cifrada) |
| Configuración → Llamadas | Grabar llamadas y aviso de grabación | `CallSettings` |
| Configuración → SMTP | Servidor de correo para las acciones de formularios | `SmtpSettings` (contraseña cifrada) |
| Configuración → Acceso por MCP | Tokens del servidor MCP y sus permisos | `McpAccessTokens` (solo el hash) |
| Extensiones | Cuentas SIP | `Extensions` (contraseña cifrada) |
| Usuarios | Usuarios del panel y sus roles | `Users` |
