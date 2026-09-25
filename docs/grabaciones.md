# 🎙️ Grabaciones

Con **Grabar llamadas** activo (Configuración → Llamadas), cada llamada que atiende el bot queda grabada.

## Quién graba

| Proveedor de voz | Cómo se graba | Formato |
|------------------|---------------|---------|
| OpenAI Realtime, Deepgram | **Mapache**: mezcla la voz de quien llama con la del bot y, si alguien toma la llamada, con la del operador | WAV PCM 16 bits 8 kHz mono (~1 MB por minuto) |
| ElevenLabs | ElevenLabs manda el audio al terminar (webhook post-llamada con *Send audio data*) | MP3 |

`CallRecorder` recibe el audio de los dos lados y, con un reloj propio de 20 ms, mezcla un cuadro de cada lado (con silencio si uno no tiene), así las pausas y la supresión de silencio no desfasan las voces. Las llamadas de menos de un segundo no se guardan.

La grabación queda enlazada a la llamada y a su id de conversación: los recados y formularios de esa llamada llevan al mismo audio (y el enlace público de un recado permite escucharlo).

## Dónde se guardan

En **Configuración → Grabaciones**:

| Destino | Qué pide | Cómo se escucha |
|---------|----------|-----------------|
| **En el servidor** | Nada: carpeta `Recordings:Path` (volumen `recordings` en Docker) | La API sirve el archivo con soporte de rangos |
| **Amazon S3 o compatible** | Bucket, región, access key y secret; para Cloudflare R2, MinIO o DigitalOcean Spaces, además la URL del servicio | Enlace firmado de 15 minutos |
| **Azure Blob Storage** | Cadena de conexión con la clave de la cuenta y contenedor (se crea si no existe) | Enlace SAS de 15 minutos |

- **Probar** sube, firma y borra un archivo de prueba con los datos del formulario antes de guardarlos.
- Opcional: un **prefijo** (carpeta) dentro del bucket o contenedor. Las grabaciones se ordenan por `año/mes`.
- Las credenciales se guardan **cifradas** y no se vuelven a mostrar.
- Cada grabación recuerda dónde quedó: cambiar de destino no mueve las anteriores y se siguen escuchando, siempre que se conserven las credenciales de ese destino (cambiar de destino no las borra).
- Sin cifrado, el reproductor del panel y la página pública de recados usan el enlace firmado directamente: no hace falta configurar CORS en el bucket.

## Cifrado

**Cifrar grabaciones** (activado por defecto) cifra cada grabación con AES-256-GCM antes de guardarla, en disco o en la nube:

- El archivo guardado empieza con `MPC1`, seguido del nonce, la etiqueta de autenticación y el audio cifrado. Si alguien lo altera, no se descifra.
- La clave se crea con la primera grabación y se guarda en `StorageSettings`, protegida con Data Protection. **Si se pierden las llaves de Data Protection, las grabaciones cifradas quedan ilegibles**; ver [seguridad](seguridad.md).
- Las cifradas no se sirven con enlace firmado: la API las descarga, las descifra y las entrega (panel y página pública de recados).
- Desactivarlo aplica solo a las grabaciones nuevas; las anteriores siguen cifradas y se siguen escuchando.

## Retención

En **Configuración → Llamadas** se definen los días que se guardan las **grabaciones** y las **transcripciones** (vacío = sin límite):

- Un trabajo de Hangfire (`retencion`) borra todos los días a las 3:00 UTC lo que supera el plazo: el archivo del almacenamiento y la fila de la base.
- **Aplicar ahora** ejecuta el borrado en el momento (`POST /api/settings/calls/retention/run`, solo administradores).
- Cada borrado queda en la [auditoría](seguridad.md#auditoría) como `retention.purged`.
- Si un archivo no se puede borrar (por ejemplo, credenciales de la nube vencidas), se reintenta al día siguiente.

## Recomendaciones

- En la nube, una **regla de ciclo de vida** del bucket o contenedor es un respaldo útil de la retención, con un plazo algo mayor.
- Informa que la llamada se graba: el aviso se agrega a las instrucciones del bot y conviene decirlo al inicio.

## Estado de las pruebas

Probado con llamadas SIP reales y OpenAI Realtime en los tres destinos: disco, S3 (servidor compatible S3Mock) y Azure (emulador Azurite). En cada uno se verificó el WAV descargado por el enlace de reproducción. El cifrado y la retención se verificaron en disco: archivo con `MPC1`, WAV correcto al escucharlo y borrado con registro de auditoría.
