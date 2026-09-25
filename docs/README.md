# 📚 Documentación del backend

Documentación del motor SIP y la API de Mapache. Para una vista general, empieza por el [README](../README.md).

## Empezar

1. [Arquitectura](arquitectura.md): cómo encajan las piezas.
2. [Instalación](instalacion.md): levantar Mapache con Docker o en local.
3. [Configuración](configuracion.md): variables de entorno y `appsettings`.
4. [ElevenLabs](elevenlabs.md): crear el agente de voz y conectarlo.

## Funcionalidades

| Guía | Para qué |
|------|----------|
| [Proveedores de voz](voz.md) | ElevenLabs, OpenAI Realtime o Deepgram |
| [Motor SIP](motor-sip.md) | Registrar cuentas SIP y recibir llamadas |
| [LLM del bot](llm.md) | Elegir proveedor de IA y entender qué contexto recibe el modelo |
| [Conocimiento](conocimiento.md) | Que el bot responda con tus documentos |
| [Herramientas](herramientas.md) | Que el bot consulte sistemas externos por HTTP o MCP |
| [Servidor MCP](servidor-mcp.md) | Que Claude, Cursor u otros agentes operen Mapache |
| [Formularios y recados](formularios.md) | Capturar datos durante la llamada y avisar a otros sistemas |
| [Campañas](campanas.md) | Llamadas salientes del bot a una lista de contactos |
| [Grabaciones](grabaciones.md) | Grabar llamadas y guardarlas en disco, S3 o Azure |

## Referencia

| Guía | Para qué |
|------|----------|
| [API](api.md) | Todos los endpoints, con sus permisos |
| [Seguridad](seguridad.md) | Autenticación, secretos, cifrado y recomendaciones |
| [Despliegue](despliegue.md) | Imágenes Docker, CI/CD, red y producción |
| [Desarrollo](desarrollo.md) | Pruebas, migraciones y convenciones de código |

## Glosario

| Término | Significado |
|---------|-------------|
| **Extensión** | Una cuenta SIP que Mapache registra (usuario, contraseña y servidor). Equivale a una cuenta de MicroSIP. |
| **Agente** | El agente de ElevenLabs Conversational AI que pone la voz. |
| **Custom LLM** | El endpoint de Mapache (`/api/llm/v1/chat/completions`) que ElevenLabs llama en cada turno en lugar de su propio LLM. |
| **Server tool** | Una herramienta del agente de ElevenLabs que llama a un endpoint HTTP de Mapache. |
| **Herramienta interna** | Una herramienta que Mapache agrega y ejecuta dentro del Custom LLM (conocimiento, HTTP, MCP) sin pasar por ElevenLabs. |
| **Recado** | Un mensaje que quien llama deja para alguien que no puede atender. |
| **Directorio** | Las personas y áreas a las que el bot puede transferir. |
