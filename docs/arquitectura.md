# 🏗️ Arquitectura

## Vista general

Mapache divide el trabajo de una llamada en tres partes:

| Parte | Quién la hace | Por qué |
|-------|---------------|---------|
| **Telefonía** (SIP, RTP) | Mapache, con SIPSorcery | Para registrarse en cualquier central, igual que un softphone, sin depender de un proveedor. |
| **Voz** (reconocimiento, síntesis, turnos, interrupciones) | ElevenLabs Conversational AI | Es la parte más difícil de hacer bien y con baja latencia. |
| **Razonamiento** (qué responder, qué herramienta usar) | Mapache como *Custom LLM* + el proveedor elegido | Para controlar el contexto, las herramientas y el proveedor sin atarse a ElevenLabs. |

```mermaid
flowchart TB
    subgraph PBX["☎️ Central SIP"]
        EXT["Extensión 100"]
    end

    subgraph API["🦝 Mapache.Api"]
        direction TB
        subgraph SIPE["Mapache.Sip"]
            ENG["SipEngine"] --> RT["SipAccountRuntime<br/>(una por cuenta)"]
            RT --> BR["CallBridge"]
            BR --> ELC["ElevenLabsConversation"]
        end
        subgraph INF["Mapache.Infrastructure"]
            ROUTER["ChatCompletionRouter"]
            CTX["BotContext<br/>instrucciones · directorio · formularios · conocimiento"]
            LOOP["SseToolInterceptor<br/>bucle de herramientas"]
            REG["BotToolRegistry<br/>conocimiento · HTTP · MCP"]
            ACT["FormActionJob"]
        end
        CTRL["Controllers REST"]
        HUB["EventsHub (SignalR)"]
        MCPS["Servidor MCP /api/mcp"]
        HF["Hangfire"]
    end

    EL["🗣️ ElevenLabs"]
    LLM["🧠 OpenAI · Gemini · Claude"]
    DB[("PostgreSQL")]

    EXT <--> RT
    ELC <--> EL
    EL --> ROUTER
    ROUTER --> CTX
    ROUTER --> LOOP
    LOOP <--> REG
    ROUTER <--> LLM
    HF --> ACT
    CTRL --> DB
    INF --> DB
    RT -. eventos .-> HUB
```

## Capas

| Proyecto | Responsabilidad | Depende de |
|----------|-----------------|------------|
| `Mapache.Domain` | Entidades (`Extension`, `Call`, `FormTemplate`...) y contratos. | nada |
| `Mapache.Infrastructure` | EF Core, cifrado (`ISecretProtector`), clientes de LLM, conocimiento, herramientas, acciones de formularios. | Domain |
| `Mapache.Sip` | Motor SIP, puente de audio, cliente WebSocket de ElevenLabs. | Domain |
| `Mapache.Api` | Composición: controllers, autenticación, SignalR, Hangfire, servidor MCP y los adaptadores que conectan el motor SIP con la base. | todos |

`Mapache.Sip` no conoce la base ni la API. Recibe lo que necesita por tres contratos que implementa la API ([`Sip/SipBindings.cs`](../src/Mapache.Api/Sip/SipBindings.cs)):

- `ISipAccountSource`: las cuentas habilitadas, con la contraseña ya descifrada.
- `IVoiceAgentSource`: la API key y el agente de ElevenLabs.
- `ISipEventSink`: registro, llamada iniciada, contestada, transcripción y fin. La API lo persiste y lo avisa por SignalR.

## Flujo de una llamada entrante

```mermaid
sequenceDiagram
    autonumber
    participant P as Central SIP
    participant R as SipAccountRuntime
    participant B as CallBridge
    participant E as ElevenLabs
    participant L as Custom LLM
    participant A as Proveedor IA
    participant S as ISipEventSink

    P->>R: INVITE
    R->>R: ¿Modo bot? ¿Hay cupo? ¿Hay agente?
    R->>S: CallStartedAsync (Ringing)
    R->>P: 200 OK (PCMU/PCMA)
    R->>B: Inicia el puente
    B->>E: WebSocket + custom_llm_extra_body.extension_id
    R->>S: CallAnsweredAsync (InProgress)
    loop Turnos
        B->>E: Audio de quien llama
        E->>L: POST /chat/completions (stream)
        L->>L: BotContext + herramientas
        L->>A: Petición al proveedor
        A-->>L: Tokens (y tool calls)
        L-->>E: SSE formato OpenAI
        E-->>B: Audio del agente
        E-->>B: Transcripción
        B->>S: CallTranscript (panel en vivo)
    end
    P->>R: BYE
    R->>S: CallEndedAsync (Completed + motivo)
```

Los casos que no terminan en el bot:

| Situación | Respuesta SIP | Estado de la llamada |
|-----------|---------------|----------------------|
| Modo *Persona* | `480 Temporarily Unavailable` | Perdida |
| Se alcanzó el límite de llamadas simultáneas | `486 Busy Here` | Perdida |
| Falta configurar ElevenLabs | `503 Service Unavailable` | Fallida, con el motivo |

## Qué pasa después de colgar

```mermaid
flowchart LR
    E["ElevenLabs"] -- "post_call_transcription<br/>post_call_audio" --> W["/api/webhooks/elevenlabs/post-call"]
    W -- "firma HMAC válida" --> R["Guarda la grabación (MP3)"]
    T["Server tool submit_form"] --> F["FormSubmission"]
    F --> H["Hangfire: una ActionExecution por acción"]
    H --> X["Webhook · Teams · Google Chat · Slack · Correo"]
    H -- "falla" --> RT["Reintentos: 30 s, 2 min, 10 min, 30 min, 1 h"]
```

## Decisiones de diseño

- **Custom LLM en vez del LLM de ElevenLabs.** Así se elige el proveedor desde el panel, se cambian las instrucciones sin tocar el agente y se inyecta contexto (directorio, conocimiento, formularios) en cada turno.
- **Herramientas dentro del Custom LLM.** Las de Mapache se ejecutan en el mismo request, sin ida y vuelta por ElevenLabs. El texto previo ("déjame revisar") sale en el momento y la voz no se corta.
- **Búsqueda de texto completo en vez de embeddings.** Responde en milisegundos, funciona con cualquier proveedor (Claude no tiene API de embeddings) y no suma costo por documento. La imagen de Postgres ya trae pgvector por si se necesita más adelante.
- **Un solo proceso.** El motor SIP, la API, Hangfire y el servidor MCP corren en el mismo contenedor. Es más simple de operar y basta para decenas de llamadas simultáneas.
- **Secretos cifrados en la base.** Las contraseñas SIP, las API keys y los secretos de webhooks van con ASP.NET Data Protection; la API nunca los devuelve.
