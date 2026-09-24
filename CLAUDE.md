# Backend — reglas

Solución .NET 10. Ver [README.md](README.md) para comandos y configuración.

## Capas

- `Mapache.Domain`: entidades y contratos. Sin dependencias de infraestructura ni de ASP.NET.
- `Mapache.Infrastructure`: EF Core (Postgres), Data Protection, integraciones. Implementa contratos de Domain.
- `Mapache.Sip`: motor SIP y bridge de audio. No depende de Infrastructure ni de Api.
- `Mapache.Api`: controllers, SignalR, Hangfire, autenticación. Es el único que compone todo.

## Convenciones

- Controllers con `[ApiController]`; DTOs como `record` junto al controller. Nunca exponer entidades de EF.
- Proyectar a DTO con `Select` **después** de filtrar/ordenar: EF no traduce predicados sobre un record construido.
- Validación con DataAnnotations y mensajes en español (`Controllers/Messages.cs`); errores como ProblemDetails.
- Enums como string en JSON y en la base (`HasConversion<string>()`).
- `TimeProvider` inyectado en lugar de `DateTime.UtcNow`, para poder testear.
- Logging con Serilog y plantillas estructuradas (`{Propiedad}`), nunca interpolación de strings. Nunca loguear secretos ni contraseñas.
- Eventos al panel por `IHubContext<EventsHub, IEventsClient>`; si se agrega un método a `IEventsClient`, escucharlo en `frontend/src/realtime/useRealtime.ts`.
- Trabajo en segundo plano (acciones, reintentos) con Hangfire, no con `Task.Run` sueltos.

## Seguridad

- Secretos solo en user-secrets (local) o variables de entorno (`backend/.env` en Docker). `appsettings*.json` lleva las claves vacías como documentación.
- Credenciales SIP cifradas con `ISecretProtector`; la API nunca las devuelve.
- Endpoints de usuarios: JWT (`[Authorize]`, roles `Admin`/`Operator`). Server tools del agente: `[Authorize(Policy = AgentToolAuthenticationHandler.PolicyName)]`. Webhooks de ElevenLabs: `[RequireElevenLabsSignature]`. No mezclar esquemas.

## Base de datos

- Migraciones en `Mapache.Infrastructure/Persistence/Migrations`; se aplican solas al arrancar.
- Crear con: `dotnet ef migrations add <Nombre> --project src/Mapache.Infrastructure --startup-project src/Mapache.Api --output-dir Persistence/Migrations`.

## Verificación

- `dotnet build` sin warnings nuevos y `dotnet test` en verde antes de dar algo por terminado.
- Lógica pura (audio, firmas, buffers) con pruebas unitarias en `tests/Mapache.Tests`.
