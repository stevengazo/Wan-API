# 🔑 Inicio de sesión: doble factor, Google y Microsoft

## Doble factor (MFA)

Cada persona lo activa en **Perfil → Doble factor**:

1. **Activar doble factor** muestra un QR (y la clave para cargarla a mano).
2. Se escanea con cualquier app autenticadora: Google Authenticator, Microsoft Authenticator, 1Password, Authy.
3. Se confirma con el primer código. Aparecen **10 códigos de recuperación**, que se muestran una sola vez.

Desde entonces, el login pide la contraseña y después el código de 6 dígitos (o uno de recuperación).

| Detalle | Cómo funciona |
|---------|---------------|
| Algoritmo | TOTP (RFC 6238): HMAC-SHA1, 6 dígitos, 30 segundos. Acepta un intervalo de desfase a cada lado. |
| Reutilización | Un código ya usado no sirve de nuevo, aunque siga dentro de su ventana. |
| Semilla | Guardada cifrada con Data Protection. |
| Códigos de recuperación | Se guarda solo su SHA-256; cada uno sirve una vez. Se regeneran desde el perfil. |
| Paso intermedio | Tras la contraseña, la API devuelve un `mfaToken` de 5 minutos. Cambiar la contraseña lo invalida. |
| Pérdida del teléfono | Un administrador lo quita en **Usuarios → Quitar doble factor** (queda en la auditoría). |

Quien entra con Google o Microsoft no pasa por el doble factor de Mapache: el segundo factor lo exige el proveedor, según la política de Google Workspace o de Entra ID.

## Google y Microsoft

Se habilitan en **Configuración → Inicio de sesión**. El formulario muestra la **URI de redirección** que hay que registrar en cada proveedor: sale de la [URL pública](configuracion.md), así que conviene configurarla primero.

### Google

1. En [Google Cloud Console](https://console.cloud.google.com/apis/credentials), ve a **APIs y servicios → Credenciales → Crear credenciales → ID de cliente de OAuth**.
2. Tipo **Aplicación web**. En **URIs de redirección autorizados**, pega la de Mapache (`https://tu-dominio/api/auth/external/google/callback`).
3. Copia el **ID de cliente** y el **secreto** en Mapache y activa **Permitir entrar con Google**.

Si la pantalla de consentimiento está en modo *Prueba*, solo entran los usuarios de prueba cargados en Google Cloud. Con tipo *Interno* (Google Workspace), solo entran las cuentas de tu organización.

### Microsoft (Entra ID)

1. En el [portal de Azure](https://portal.azure.com), ve a **Microsoft Entra ID → Registros de aplicaciones → Nuevo registro**.
2. En **URI de redirección**, elige plataforma **Web** y pega la de Mapache (`https://tu-dominio/api/auth/external/microsoft/callback`).
3. En **Certificados y secretos**, crea un secreto de cliente y copia su **valor** (no su ID).
4. En Mapache, carga el **ID de aplicación (cliente)** y el secreto, y define **Cuentas aceptadas**:

| Valor | Quién entra |
|-------|-------------|
| ID del inquilino (GUID) | Solo las cuentas de tu empresa. **Recomendado.** |
| `organizations` | Cualquier cuenta de trabajo o escuela |
| `common` | Cuentas de trabajo y personales (outlook.com, hotmail.com) |
| `consumers` | Solo cuentas personales |

### Registro y vinculación

- **Vincular:** con la sesión abierta, en **Perfil → Cuentas vinculadas**. Es la forma de sumar Google o Microsoft a una cuenta existente.
- **Registro:** con **Crear la cuenta la primera vez que alguien entra**, quien entra con un proveedor sin cuenta previa recibe una con rol **Operador**. **Dominios permitidos** limita qué correos pueden registrarse.
- **Nunca se vincula por correo:** si ya existe un usuario con ese correo, se pide entrar con la contraseña y vincular desde el perfil. Así, quien controle ese correo en otro proveedor no puede quedarse con la cuenta ni saltar su doble factor. Microsoft, además, no garantiza que el correo esté verificado (ataque *nOAuth*).
- Quien se registró con un proveedor no tiene contraseña: puede definir una en el perfil. No puede desvincular su única forma de entrar.

### Cómo funciona por dentro

- OpenID Connect con flujo de código y **PKCE**. El `state` viaja cifrado y con vencimiento (10 minutos), y se ata al navegador con una cookie `HttpOnly` (anti-CSRF).
- El `id_token` se valida con las llaves publicadas por el proveedor: firma, audiencia, vencimiento, `nonce` y emisor. En Microsoft, el emisor se arma con el `tid` de la cuenta y se compara con las cuentas aceptadas.
- La cuenta se identifica por el claim `sub` del proveedor, que no cambia, y nunca por el correo.
- Al terminar, la API redirige a `/login/sso#token=…`. El token va en el fragmento, que el navegador no envía a ningún servidor; el panel lo borra de la barra de direcciones.
- Los secretos de cliente se guardan cifrados. Cada inicio de sesión, alta, vinculación y desvinculación queda en la auditoría.
