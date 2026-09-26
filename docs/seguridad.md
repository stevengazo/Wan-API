# 🔒 Seguridad

## Secretos

| Qué | Dónde vive | Cómo se protege |
|-----|------------|-----------------|
| Clave JWT, token del agente, secreto del webhook | user-secrets (local) o variables de entorno (`backend/.env`) | Fuera de git (`.gitignore`) |
| Contraseñas SIP | Tabla `Extensions` | Cifradas con ASP.NET Data Protection (`ISecretProtector`) |
| API keys de OpenAI, Gemini, Anthropic y ElevenLabs | `LlmProviderSettings`, `VoiceSettings` | Cifradas |
| Headers de peticiones HTTP y servidores MCP | `HttpTools`, `McpServerConnections` | Cifrados |
| Secretos de webhooks y contraseña SMTP | `FormActions`, `SmtpSettings` | Cifrados |
| Tokens MCP | `McpAccessTokens` | Solo el SHA-256; el valor se muestra una vez |
| Semilla del doble factor | `Users` | Cifrada; de los códigos de recuperación, solo el SHA-256 |
| Secretos de cliente de Google y Microsoft | `SsoSettings` | Cifrados |
| Contraseñas de usuarios | `Users` | `PasswordHasher` de ASP.NET Identity |
| Grabaciones | Disco, S3 o Azure | AES-256-GCM con clave propia, protegida con Data Protection ([detalle](grabaciones.md#cifrado)) |

La API **nunca devuelve** un secreto: solo dice si está cargado. Al editar, un campo vacío conserva el valor anterior.

### Llaves de Data Protection

Viven en la tabla `DataProtectionKeys`. **Si se pierden, los secretos guardados quedan ilegibles** y hay que volver a cargarlos. Incluye esa tabla en tus respaldos.

Las llaves se cifran en reposo con una **clave maestra** que vive fuera de la base, así un respaldo de la base por sí solo no permite leer los secretos:

| Variable | Qué hace |
|----------|----------|
| `DataProtection__KeyEncryptionKey` | Clave AES-256 en base64 (`openssl rand -base64 32`). Recomendada. |
| `DataProtection__CertificatePath` + `DataProtection__CertificatePassword` | Alternativa: un certificado `.pfx` montado en el contenedor. |

- Al activarla en una instalación existente, la llave en uso estaba sin cifrar: al arrancar se crea una nueva cifrada para lo que se guarde en adelante. La anterior se conserva (sin cifrar) para leer lo ya guardado hasta que venza, a los 90 días.
- **No cambies la clave maestra:** las llaves cifradas con la anterior quedan ilegibles, y el error dice qué clave esperaba (un identificador, nunca la clave).
- Guárdala en un gestor de secretos y respáldala aparte de la base.

## Autenticación

- **Panel:** JWT firmado con HMAC-SHA256, 8 horas por defecto. Roles `Admin` y `Operator`.
- **Sesión en cookie:** el JWT viaja en la cookie `wan_session` (`HttpOnly`, `SameSite=Strict`, `Secure` con HTTPS). JavaScript no la puede leer, así un XSS no puede robar la sesión. En `POST`, `PUT` y `DELETE` la cookie solo vale con el header `X-Wan-Client` (anti-CSRF: otro sitio no puede agregarlo sin preflight CORS, y la API no habilita CORS). Cerrar sesión borra la cookie; el JWT no se revoca en el servidor y vence solo.
- **Doble factor:** TOTP opcional por usuario, con códigos de recuperación. Ver [inicio de sesión](inicio-de-sesion.md).
- **Google y Microsoft:** OpenID Connect con PKCE; nunca se vincula una cuenta por coincidencia de correo.
- **Login y registro:** 10 intentos por minuto por IP. Detrás de un proxy, activa `ReverseProxy:Enabled` para que la IP sea la real. Sin proxy déjalo apagado, porque el cliente podría falsear `X-Forwarded-For`.
- **ElevenLabs → Wan:** Custom LLM y server tools con un bearer token propio; webhook post-llamada con firma HMAC-SHA256 y una ventana de 30 minutos contra repeticiones.
- **Clientes MCP:** tokens revocables con permisos por herramienta.
- **Webhooks salientes:** firmados con `X-Wan-Signature` si la acción tiene secreto.

## Auditoría

**Auditoría** (solo administradores) registra quién hizo qué, cuándo y desde qué IP. Se filtra por días, acción y texto, y se exporta a CSV (`GET /api/audit?format=csv`).

| Área | Acciones |
|------|----------|
| Sesión | Inicios de sesión correctos y fallidos (con el método: contraseña, doble factor, Google, Microsoft), cambio de contraseña, doble factor activado o quitado, cuentas vinculadas y desvinculadas |
| Usuarios | Alta, cambios, restablecimiento de contraseña, baja |
| Grabaciones | Escuchada o descargada, borrada |
| Recados | Enlace público creado, abierto por un tercero, borrado |
| Configuración | Cambios en voz, llamadas, almacenamiento, SMTP y demás |
| MCP | Tokens creados, cambiados o revocados |
| Campañas | Iniciadas, pausadas, borradas, contactos importados |
| Retención | Borrado automático de grabaciones y transcripciones |

El registro se escribe aparte de la operación: si falla, la operación no se cae y el error queda en los logs.

## Antes de exponer Wan a internet

- [ ] **Cerrar el registro abierto:** `/api/auth/register` crea administradores. Restríngelo o quítalo una vez creado tu usuario.
- [ ] Servir todo por **HTTPS** (ElevenLabs lo exige, y los JWT viajan en headers).
- [ ] Generar `Auth:Jwt:SigningKey` y `AgentWebhooks:ToolToken` aleatorios y largos.
- [ ] Usar una contraseña fuerte para `POSTGRES_PASSWORD` y no publicar el puerto 5432 (el compose lo deja solo en `127.0.0.1`).
- [ ] Definir `DataProtection__KeyEncryptionKey` y guardarla aparte de los respaldos.
- [ ] Respaldar la base, incluida `DataProtectionKeys`, y el volumen de grabaciones.
- [ ] Revisar la normativa de grabación de llamadas y de datos personales de tu país.
- [ ] Limitar los puertos SIP y RTP del firewall a las IPs de tu central o proveedor.

## Reportar una vulnerabilidad

Si encuentras un problema de seguridad, **no abras un issue público**. Escríbele al mantenedor por correo o usa *Security → Report a vulnerability* de GitHub, con los pasos para reproducirlo.
