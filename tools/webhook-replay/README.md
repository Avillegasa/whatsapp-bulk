# webhook-replay (F0.8)

Firma payloads de webhook de WhatsApp de ejemplo con el App Secret, igual que Meta (`X-Hub-Signature-256` = `sha256=` + HMAC-SHA256 en hex del cuerpo), y los envía al receptor local. Sirve para reproducir casos que Meta no genera a pedido: duplicados, desorden, lotes, bajas de plataforma y plantillas pausadas.

La firma se calcula con una implementación propia (no la del receptor), así la herramienta comprueba al receptor de forma independiente.

## Requisitos

1. `Meta:AppSecret` en user-secrets (el mismo almacén de Api, Webhooks y Worker, D-018). Para `--intento`, además `ConnectionStrings:WaBulk`.
2. Receptor corriendo (otra ventana): `dotnet run --project src\backend\WaBulk.Webhooks`
3. Worker corriendo (otra ventana) para que el inbox se procese: `dotnet run --project src\backend\WaBulk.Worker`
4. No hace falta el túnel: la herramienta habla directo con `http://localhost:5244`.

## Uso (cmd, desde la raíz del repo)

```
dotnet run --project tools\webhook-replay -- lista
dotnet run --project tools\webhook-replay -- <escenario> [--intento ultimo|<AttemptId>] [--wamid <wamid>] [--url <url>] [--firma-mala]
```

- `--intento ultimo`: lee de SQL el último `SendAttempt` con wamid y usa su AttemptId, wamid y teléfono. Así el procesador correlaciona los estados como si vinieran de Meta. Con `--intento <AttemptId>` se usa ese intento.
- Sin `--intento`: AttemptId y wamid sintéticos. Las filas de estado no se correlacionan: quedan `EnEspera` y, tras 8 intentos (~35 min), en `Revision`. Es el caso de un estado desconocido.
- `--firma-mala`: firma con otra clave. El receptor debe responder 401 y no guardar nada.
- Código de salida: 0 si el receptor respondió lo esperado (200, o 401 con `--firma-mala`), 2 si no y 1 por error de uso o de conexión.

Ver el resultado:

```
docker exec -i wabulk-sqlserver sh -c "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -d WaBulk -W" < db\scripts\verificar-f0.8.sql
```

## Escenarios

| Escenario | Comando | Qué envía | Resultado esperado |
| --- | --- | --- | --- |
| Duplicado | `dotnet run --project tools\webhook-replay -- duplicado --intento ultimo` | El mismo `delivered` dos veces (bytes idénticos), como cuando Meta reintenta | 2 filas en `WebhookInbox`; 1 solo `MessageEvent` Delivered |
| Desorden | `dotnet run --project tools\webhook-replay -- desorden --intento ultimo` | `read`, `delivered` y `sent`, en ese orden (timestamps T+2, T+1, T) | 3 filas; `MessageEvent` Read, Delivered y Sent; `EstadoEntrega` = Read (no retrocede) |
| Lote | `dotnet run --project tools\webhook-replay -- lote --intento ultimo` | 1 POST con 2 `entry`: `sent` + `delivered` en la primera, `read` en la segunda | 1 fila; 3 `MessageEvent`; `EstadoEntrega` = Read |
| Baja de plataforma | `dotnet run --project tools\webhook-replay -- stop` | `user_preferences` con `value` = `stop`, `category` = `marketing_messages` | 1 fila `Procesado` sin eventos; 1 `PlatformPreference` Stop |
| Alta de plataforma | `dotnet run --project tools\webhook-replay -- resume` | `user_preferences` con `value` = `resume` | 1 fila `Procesado` sin eventos; 1 `PlatformPreference` Resume; no toca `ConsentRecord` (nunca levanta una baja de la empresa) |
| Baja por palabra clave | `dotnet run --project tools\webhook-replay -- baja-palabra` | Mensaje entrante de texto `BAJA` del contacto | 1 fila `Procesado`; 1 `ConsentRecord` OptOut, Fuente `PalabraClave` |
| Baja por botón | `dotnet run --project tools\webhook-replay -- baja-boton` | Mensaje `button` con payload `BAJA_PROMOCIONES` y texto "No recibir más" | 1 fila `Procesado`; 1 `ConsentRecord` OptOut, Fuente `Boton` |
| Plantilla pausada | `dotnet run --project tools\webhook-replay -- plantilla-paused` | `message_template_status_update` con `event` = `PAUSED`, `other_info.title` = `FIRST_PAUSE` | 1 fila `Procesado`; 1 `Alert` PlantillaPausada (Origen Webhook) y pausa global (`DispatchPause`). Levantarla después con `dotnet run --project src\backend\WaBulk.Worker -- reanudar` (queda registrado el usuario de Windows) |
| Firma inválida | `dotnet run --project tools\webhook-replay -- lote --firma-mala` | El lote, firmado con otra clave | 401 en cada POST; ninguna fila nueva |

Notas:
- `MessageEvent` es único por (wamid, estado). Si repites un escenario sobre el mismo intento, los eventos ya registrados cuentan como duplicados y no se crean de nuevo. Para ver el resultado "limpio", usa un intento sin eventos (`--intento <AttemptId>`) o haz antes un `send-test`.
- La herramienta escribe estados falsos (por ejemplo `read`) sobre intentos reales de desarrollo. No usarla contra producción.

## Plantillas

`payloads/*.json` se embeben en el ensamblado. Marcadores que se sustituyen:

| Marcador | Valor |
| --- | --- |
| `{{WABA_ID}}`, `{{PHONE_NUMBER_ID}}`, `{{DISPLAY_PHONE}}` | `appsettings.json` (sección `Replay`) |
| `{{WA_ID}}` | Teléfono del `DispatchJob` sin `+` (con `--intento`) o `Replay:WaId` |
| `{{WAMID}}`, `{{ATTEMPT_ID}}` | Del intento, o sintéticos |
| `{{TS}}`, `{{TS1}}`, `{{TS2}}` | Hora actual en segundos Unix, +1 y +2 |

`user_preferences` y `message_template_status_update` llevan `{{TS}}` sin comillas (son enteros en la referencia de Meta), por eso los archivos no son JSON válido hasta que se sustituyen los marcadores. Las pruebas (`WaBulk.Tests/Tools/WebhookReplayTests.cs`) comprueban que cada plantilla, ya con los valores sustituidos, es JSON válido y que su firma la acepta el receptor.

Fuentes (forma de los payloads):
- Estados: https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/reference/messages/status
- `user_preferences`: https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/reference/user_preferences
- `message_template_status_update` (la doc no trae un ejemplo de `PAUSED`; se usó la sintaxis con `event` = `PAUSED` y `other_info`): https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/reference/message_template_status_update
- Firma: https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/create-webhook-endpoint
