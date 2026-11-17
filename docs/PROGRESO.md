# Progreso

**Fase actual:** Fase 0 — PoC
**Siguiente tarea:** F0.9 (Claude Code) — en `C:\Users\avillegas\dev\whatsapp-bulk`: "Sigamos con la tarea F0.9"
**Última actualización:** 2026-11-17

## Estado de tareas

Estados: Pendiente · En curso · Hecho (sin verificar) · Verificado · Bloqueado

| ID | Tarea | Quién | Estado | Evidencia |
| --- | --- | --- | --- | --- |
| F0.1 | App de Meta y número de prueba | Tú | Verificado | `hello_world` recibido (confirmación de Andrés); App, WABA, Phone Number y System User IDs en META-SETUP.md; token del System User (sin caducidad) en gestor de contraseñas; grep `EAA…` sin resultados en la carpeta |
| F0.2 | Repositorio en GitHub | Tú | Verificado | Repo privado Avillegasa/whatsapp-bulk (confirmación de Andrés); `git push origin main` 8176d15..bdafc0e; `dev` al día con `origin/dev`; `git status` limpio; sin `.env` en el commit |
| F0.3 | Esqueleto de la solución .NET | Claude Code | Verificado | `dotnet --version` 10.0.404; `dotnet build` 0 advertencias, 0 errores; `dotnet test` 2/2 (`/health` en Api y Webhooks → 200 `Healthy`); sin secretos (git grep `EAA…` vacío) |
| F0.4 | SQL Server local y migración inicial | Claude Code | Verificado | Contenedor `wabulk-sqlserver` `Up (healthy)`; `dotnet ef database update` → `Done.` (InitialCreate, EF 10.0.15); `sys.tables` → DispatchJob, MessageEvent, SendAttempt, WebhookInbox; `sys.indexes` → `IX_SendAttempt_Wamid` filtro `([Wamid] IS NOT NULL)`; `dotnet test` 5/5; build 0 advertencias |
| F0.5 | Cliente de Meta y envío con SendAttempt | Claude Code + Tú | Verificado | `dotnet test` 14/14 (200 → Accepted, 131026 → Rejected, timeout → SubmissionUnknown, intento confirmado antes del POST, un solo POST); envío real `send-test`: `SendAttempt` 01A1E617… `Accepted`, HTTP 200, `wamid.HBgL…` guardado (consulta SQL) y mensaje recibido (confirmación de Andrés); build 0 advertencias; sin versión literal, retry ni tokens |
| F0.6 | Receptor de webhooks | Claude Code + Tú | Verificado | `dotnet test` 22/22 (firma válida → 200 y fila; inválida, ausente o cuerpo alterado → 401 sin fila; > límite → 413; GET token válido → challenge, inválido → 403); "Verify and save" en Meta OK (log "Handshake de webhook verificado"); webhook de prueba del panel → `WebhookInbox` Id=2 (555 bytes, confirmación de Andrés + SQL); además estados reales `delivered`/`sent` → Id=3/4 con AttemptId 01A1E975… (D-021); build 0 advertencias; sin secretos |
| F0.7 | Procesador de eventos | Claude Code | Verificado | `dotnet test` 36/36 (14 nuevas: duplicado en dos webhooks y en el mismo lote → 1 evento; `read` antes de `delivered` no retrocede; webhook antes de guardar el wamid → correlación por AttemptId; secuencia `sent`→`delivered`→`read` con payloads de la referencia oficial; lote con varias entry; EnEspera → Procesado y → Revision tras MaxAttempts; SubmissionUnknown resuelto); envío real (WebhookInbox Id=3 `delivered` e Id=4 `sent`, en desorden) → 2 `MessageEvent` con AttemptId 01A1E975… y `EstadoEntrega` = Delivered (`verificar-f0.7.sql`); build 0 advertencias; sin secretos |
| F0.8 | Herramienta de replay firmado | Claude Code | Verificado | `dotnet test` 45/45 (9 nuevas: las 7 plantillas → JSON válido y firma aceptada por `WebhookSignature`; duplicado/desorden/lote/stop/resume/PAUSED pasando por receptor + procesador; firma mala → 401 sin filas); corrida real, un comando por escenario, contra el receptor local y el Worker: firma mala → 401; duplicado → inbox 5–6 y 1 `MessageEvent` Delivered; desorden → inbox 7–9, Read/Delivered/Sent, `EstadoEntrega` Read; lote → inbox 10, `EstadoEntrega` Read; stop/resume/PAUSED → inbox 11–13 `Procesado` sin eventos; los 7 escenarios en el README; build 0 advertencias; sin secretos |
| F0.9 | Despachador con cola y lease | Claude Code | Pendiente | |
| F0.10 | Bajas | Claude Code | Pendiente | |
| F0.11 | Concurrencia y capacidad | Claude Code | Pendiente | |
| F0.12 | Alertas simuladas | Claude Code | Pendiente | |

## Pendientes y bloqueos

- Definir dónde corre SQL Server en producción (D-006).
- Crear el portafolio de Meta de la empresa antes de F1.1 (D-015).
- Actualizar la ruta de la carpeta (D-016) en las instrucciones del proyecto de claude.ai y en la skill `whatsapp-bulk-guia`; reconectar la carpeta en Claude desktop.
- Confirmar con el área comercial o legal cómo consta el consentimiento de los clientes para promociones (antes de la Fase 2).
- Agregar FK de `DispatchJob.CampaignVersionId` y `DispatchJob.ContactId` cuando existan `CampaignVersion` y `Contact` (Fase 1).
- Índice de reclamo de la cola en `DispatchJob` (EstadoDespacho, ProximoIntentoUtc/ProgramadoUtc) en F0.9.
- F0.9: al recuperar un intento en `Submitting`, si ya tiene `Wamid` o un `MessageEvent` con su AttemptId (lo puso el procesador), pasarlo a `Accepted` en vez de `SubmissionUnknown`; y pasar el `DispatchJob` de `NeedsReview` a `Submitted` cuando el procesador resuelve un `SubmissionUnknown` (hoy solo cambia el intento).
- El procesador ignora (marca Procesado sin eventos) los cambios sin `statuses`: mensajes entrantes, `user_preferences`, `account_update`, plantillas, calidad. Se manejan en F0.10 (bajas) y Fase 1; el estado `played` también se ignora.
- Estados de mensajes enviados fuera del sistema con el mismo número (sin AttemptId ni wamid conocido) terminan en `Revision` tras 8 intentos (~35 min): revisar si conviene descartarlos al conectar el número real (F1.1).
- Un solo procesador del inbox por ahora; con varias instancias del Worker, reclamo con lease o `READPAST` (F0.11).
- F0.9: actualizar `DispatchJob.EstadoDespacho` según el resultado del intento, verificar el lease antes de guardar y pasar a `SubmissionUnknown` los intentos que quedaron en `Submitting` (hoy `TemplateSendService` solo actualiza el `SendAttempt`).
- Plantillas con parámetros (`components`) en `TemplateMessage`: con las campañas (F0.9 / Fase 1).
- Datos de desarrollo: los intentos 01A1E617… y 01A1E975… quedaron con `EstadoEntrega` = Read por el replay de F0.8 (no es una lectura real).

## Bitácora (lo más reciente arriba)

### 2026-11-17 — Verificación manual de `verificar-f0.8.sql` (Andrés)
- Corrido desde cmd: `WebhookInbox` 1–13 en `Procesado`, 1 intento, sin error (5–6 duplicado, 7–9 desorden, 10 lote, 11–13 stop/resume/PAUSED); último intento 01A1E986… `Accepted`/`Delivered` con 1 `MessageEvent` Delivered.
- Cierra el pendiente "confirmar `verificar-f0.8.sql` en cmd". Sigue abierto: `EstadoEntrega` = Read de prueba en 01A1E617…/01A1E975….
- Siguiente: F0.9.

### 2026-11-17 — F0.8 Herramienta de replay firmado (Claude Code)
- Hecho: consola `tools/webhook-replay` (proyecto `WaBulk.WebhookReplay`, en la solución, carpeta `tools`). Arma el webhook desde plantillas `payloads/*.json` embebidas, lo firma con `Meta:AppSecret` (user-secrets compartido, D-018; HMAC propio, independiente del receptor) y lo envía a `http://localhost:5244/webhooks/whatsapp`. Escenarios `duplicado`, `desorden`, `lote`, `stop`, `resume`, `plantilla-paused` y el modificador `--firma-mala`. `--intento ultimo|<AttemptId>` lee de SQL el AttemptId, el wamid y el teléfono reales para que el procesador correlacione; sin él usa ids sintéticos. Script `db/scripts/verificar-f0.8.sql`.
- Verificación: `dotnet build` → 0 advertencias (con `--artifacts-path` aparte: el Worker de Andrés tenía las DLL bloqueadas); `dotnet test` → 45/45; corrida real (receptor levantado por Claude Code, Worker de Andrés): `lote --firma-mala` → 401 sin fila; `duplicado --intento ultimo` (01A1E986…) → inbox 5–6, 1 `MessageEvent` Delivered; `desorden --intento 01A1E617…` → inbox 7–9, eventos Read, Delivered y Sent, `EstadoEntrega` = Read; `lote --intento 01A1E975…` → inbox 10, solo `read` nuevo (sent y delivered ya existían), `EstadoEntrega` = Read; `stop`, `resume`, `plantilla-paused` → inbox 11–13 `Procesado` sin eventos; `lista` probado desde cmd; git grep sin `EAA…`, sin versión literal ni retry.
- Fuentes: https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/reference/user_preferences · https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/reference/message_template_status_update (sin ejemplo de `PAUSED`: se armó con la sintaxis documentada) · https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/reference/messages/status
- Archivos: `tools/webhook-replay/{WaBulk.WebhookReplay.csproj,Program.cs,Replay.cs,appsettings.json,README.md,payloads/*.json}`, `src/backend/WaBulk.Tests/Tools/WebhookReplayTests.cs`, `src/backend/WaBulk.sln`, `db/scripts/verificar-f0.8.sql`, `CLAUDE.md`.
- Nota técnica: `Microsoft.Data.SqlClient` 6.1.7 trae `System.Security.Cryptography.Pkcs` 9.0.11 (vulnerable, NU1903); el proyecto fija 10.0.13, la misma versión que ya resuelve el resto de la solución vía EF.
- Decisiones: ninguna.
- Pendientes: `EstadoEntrega` = Read de prueba en dos intentos de desarrollo; confirmar `verificar-f0.8.sql` en cmd.
- Siguiente: F0.9.

### 2026-11-17 — Verificación manual de scripts SQL (Andrés)
- `verificar-f0.4.sql`: 4 tablas; `IX_SendAttempt_Wamid` filtrado `([Wamid] IS NOT NULL)`; migraciones `InitialCreate` e `InboxProcessing` (10.0.15).
- `verificar-f0.6.sql`: WebhookInbox 1–4 (account_update; prueba del panel con EntryId 0; `delivered` y `sent` reales del AttemptId 01a1e975…).
- `verificar-f0.7.sql`: inbox 1–4 `Procesado` (Intentos 1, sin error); MessageEvent `Delivered` y `Sent` del 01A1E975…; `EstadoEntrega` = Delivered.
- Nuevo intento 01A1E986… `Accepted` sin webhooks (`EstadoEntrega` NULL): enviado con receptor o túnel apagados; no es un error.

### 2026-11-17 — F0.7 Procesador de eventos (Claude Code)
- Hecho: `WebhookInboxProcessor` (Infrastructure/Messaging) toma filas `Pendiente` y `EnEspera` vencidas, separa entry/changes/statuses y correlaciona cada estado con su `SendAttempt`: primero por AttemptId (`biz_opaque_callback_data`), luego por wamid. Registra `MessageEvent` una sola vez por (wamid, estado) y hace avanzar `SendAttempt.EstadoEntrega` sin retroceder. Si el intento no tiene wamid se lo completa; un `SubmissionUnknown` pasa a `Accepted`. Sin correlación → `EnEspera` (30 s, 60 s… tope 10 min); tras 8 intentos, JSON inválido, wamid distinto o intento rechazado → `Revision` con el motivo en `Error`. Corre en el Worker (`InboxProcessorWorker`, cada 2 s, sección `InboxProcessor`). Migración `InboxProcessing`. Script `db/scripts/verificar-f0.7.sql`.
- Verificación: `dotnet test` → 36/36 (14 del procesador); build 0 advertencias (salida aparte: el receptor de Andrés tenía bloqueadas las DLL de Webhooks); `dotnet ef database update` → Done; Worker contra la base real → WebhookInbox 1–4 `Procesado` (1 y 2 sin estados, ignorados); `MessageEvent` 1 `Delivered` y 2 `Sent` del intento 01A1E975…, mismo `TimestampMeta` 12:22:20; `SendAttempt.EstadoEntrega` = `Delivered` (no retrocedió con el `sent` posterior); git grep sin `EAA…`, versión literal ni Polly.
- Fuentes: https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/reference/messages/status (valores de `status` incluido `played`; `timestamp` en segundos Unix; `pricing` en uno solo de los webhooks; `delivered` puede omitirse si el mensaje se lee de inmediato).
- Archivos: `src/backend/WaBulk.Infrastructure/Messaging/{WebhookInboxProcessor,WebhookPayload,InboxProcessorOptions,MessagingServiceCollectionExtensions}.cs`, `src/backend/WaBulk.Worker/{InboxProcessorWorker.cs,Program.cs,appsettings.json}`, `src/backend/WaBulk.Domain/Messaging/{SendAttempt,WebhookInbox}.cs`, migración `20261117123538_InboxProcessing`, `src/backend/WaBulk.Tests/Messaging/WebhookInboxProcessorTests.cs`, `db/scripts/verificar-f0.7.sql`.
- Decisiones: D-023.
- Pendientes: ver "Pendientes y bloqueos" (recuperación en F0.9, campos sin statuses, un solo procesador).
- Siguiente: F0.8.

### 2026-11-17 — F0.6 aceptada (Claude desktop)
- CIERRE F0.6 revisado: commits 62b3d05 y 94d90af; `Meta:WabaId` = 2088365445132575; sin tokens en el repo.
- D-022: no se rotan App Secret ni Verify Token (riesgo aceptado, app descartable).
- PLAN.md: verificación de F0.7 incluye un envío real `sent`/`delivered` (D-021).

### 2026-11-17 — F0.6 Receptor de webhooks (Claude Code + Andrés)
- Hecho: proyecto Webhooks en `http://localhost:5244`. GET `/webhooks/whatsapp` hace el handshake: responde con `hub.challenge` si el verify token coincide (comparación en tiempo constante), 403 si no. POST valida `X-Hub-Signature-256` (HMAC-SHA256 con el App Secret) sobre los bytes crudos con `FixedTimeEquals`, limita el cuerpo (5 MB, 413), guarda en `WebhookInbox` (Pendiente) y recién responde 200. Sin secretos configurados responde 503 para que Meta reintente. Corregido `Meta:WabaId` = 2088365445132575 (Worker y payload de prueba). Script `db/scripts/verificar-f0.6.sql`.
- Verificación: `dotnet build` → 0 advertencias; `dotnet test` → 22/22 (8 del receptor); "Verify and save" en Meta OK; webhook de prueba del panel guardado (Id=2, 555 bytes, `entry.id` 0 = muestra de Meta); `send-test` real (AttemptId 01A1E975…, Accepted) → Id=3 `delivered` e Id=4 `sent`, con el AttemptId en `biz_opaque_callback_data` (`verificar-f0.6.sql`); git grep sin `EAA…`, sin versión literal ni Polly; `.env` ignorado.
- Fuentes: https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/create-webhook-endpoint · https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/overview
- Archivos: `src/backend/WaBulk.Webhooks/{Program.cs,WhatsAppWebhookEndpoints.cs,WebhookOptions.cs,Security/WebhookSignature.cs}`, `src/backend/WaBulk.Tests/Webhooks/WhatsAppWebhookTests.cs`, `src/backend/WaBulk.Worker/appsettings.json`, `db/scripts/verificar-f0.6.sql`, `CLAUDE.md`.
- Decisiones: D-021 (sin publicar la app sí llegan estados reales de envíos al número permitido; llegaron en desorden).
- Incidente: el App Secret y el Verify Token se pegaron en el chat → rotar (Pendientes). No se guardaron en ningún archivo.
- Pendientes: índice por `Estado` en `WebhookInbox` (F0.7).
- Siguiente: F0.7.

### 2026-11-17 — F0.6: suscripción de la app (Claude desktop)
- Token del System User rotado otra vez (expuesto en el chat); cargado solo en `dotnet user-secrets`.
- `POST /v27.0/2088365445132575/subscribed_apps` → `{"success":true}`; `GET` lista `WaBulk PoC` (1669296924583840) y `WA DevX Webhook Events 1P App` (app de Meta).
- Webhook: handshake verificado en Meta (log "Handshake de webhook verificado"); primer POST guardado en `WebhookInbox` Id=1 (317 bytes).
- Decisión: D-019 — publicar la app PoC; reemplazada por D-020 (publicar exige verificación del negocio y App Review): F0.6 con webhooks de prueba del panel, F0.7 con replay; PLAN.md ajustado.
- Campos suscritos: account_alerts, account_review_update, account_update, business_capability_update, calls, message_template_quality_update, message_template_status_update, messages, phone_number_name_update, phone_number_quality_update, security, template_category_update, user_preferences.
- El panel "Revisa los webhooks de prueba" muestra `entry.id` = 2088365445132575 (confirma WABA ID = Messaging account ID).
- Método para usar el token en cmd sin mostrarlo: `for /f` sobre `dotnet user-secrets list` → `%WA_TOKEN%`, y `set WA_TOKEN=` al terminar.

### 2026-11-16 — F0.6 preparación (Claude desktop)
- Token del System User rotado tras exponerse en el chat (Revocar tokens → Generar token); nuevo en KeePass y en `dotnet user-secrets` (confirmación de Andrés).
- KeePass: entradas de App Secret y Verify Token listas.
- Dev Tunnels CLI 1.0.2115 instalado; sesión iniciada con la cuenta Microsoft del trabajo.
- Puerto del receptor: 5244 (META-SETUP.md sección B actualizada).
- Corrección de IDs: según la doc de Meta (account-model-evolution), el WABA ID = Messaging account ID 2088365445132575; 2043065989689955 es la WhatsApp account (WAAC). `subscribed_apps` usa 2088…. Pendiente para Claude Code: `Meta:WabaId`.

### 2026-11-16 — F0.5 Cliente de Meta y envío con SendAttempt (Claude Code + Andrés)
- Hecho: `MetaOptions` (sección `Meta`, versión de Graph API por configuración) y `MetaWhatsAppClient` (HttpClient tipado, sin resiliencia/retry, timeout configurable) que clasifica: 2xx con `messages[0].id` → Accepted; error con `error.code` → Rejected; timeout, conexión cortada, cancelación, 5xx sin cuerpo o 2xx sin id → SubmissionUnknown. `TemplateSendService`: inserta y confirma `SendAttempt` (Submitting, AttemptId = Guid v7) antes del POST, envía `biz_opaque_callback_data` = AttemptId y guarda el resultado aunque se cancele. Comando `send-test` en el Worker (DispatchJob de prueba con CampaignVersionId = 0). `UserSecretsId` compartido en `Directory.Build.props` (D-018). Andrés cargó `Meta:AccessToken` en user-secrets.
- Verificación: `dotnet build` → 0 advertencias; `dotnet test` → 14/14 (9 nuevas: 200, 131026, timeout, conexión cortada, 502 vacío, 200 sin id, URL con versión configurada y callback data, intento visible como Submitting desde otro contexto durante el POST, timeout → un solo POST); envío real `send-test` → `SendAttempt` 01A1E617-2789-7751-99A1-2D17AE607B7B `Accepted`, HTTP 200, `message_status=accepted`, `wamid.HBgL…` guardado (consulta en `SendAttempt`); mensaje recibido en el teléfono (confirmación de Andrés); git grep sin `graph.facebook.com/vNN`, sin `AddStandardResilienceHandler`/Polly, sin `EAA…`.
- Fuentes: https://developers.facebook.com/documentation/business-messaging/whatsapp/messages/send-messages · https://developers.facebook.com/documentation/business-messaging/whatsapp/reference/whatsapp-business-phone-number/message-api · https://developers.facebook.com/documentation/business-messaging/whatsapp/support/error-codes · https://developers.facebook.com/docs/whatsapp/cloud-api/reference/messages/ (biz_opaque_callback_data, máx. 512).
- Archivos: `src/backend/WaBulk.Infrastructure/Meta/*`, `src/backend/WaBulk.Infrastructure/Messaging/TemplateSendService.cs`, `src/backend/WaBulk.Worker/{Program.cs,SendTestCommand.cs,appsettings.json}`, `src/backend/WaBulk.Tests/Meta/*`, `src/backend/Directory.Build.props`, `CLAUDE.md`, `docs/META-SETUP.md`.
- Decisiones: D-018 (user-secrets compartido).
- Pendientes: estado del DispatchJob, lease y recuperación de `Submitting` (F0.9); parámetros de plantilla.
- Siguiente: F0.6.

### 2026-11-16 — F0.4 SQL Server local y migración inicial (Claude Code)
- Hecho: `deploy/docker/compose.dev.yml` (SQL Server 2022 Developer, `127.0.0.1:14333`, clave `SQLSERVER_SA_PASSWORD` desde `.env`, volumen `wabulk-sqldata`, healthcheck con `mssql-tools18`). Entidades `WebhookInbox`, `DispatchJob`, `SendAttempt`, `MessageEvent` y enums de estado en `WaBulk.Domain/Messaging`; `WaBulkDbContext` + configuraciones en `WaBulk.Infrastructure/Persistence` (columnas con nombres de ARQUITECTURA, enums como texto); migración `InitialCreate`; `dotnet-ef` 10.0.15 como herramienta local (`dotnet-tools.json`); cadena de conexión en user-secrets de Api; `db/scripts/verificar-f0.4.sql`.
- Índices: único filtrado `SendAttempt(Wamid) WHERE [Wamid] IS NOT NULL`; únicos `DispatchJob(CampaignVersionId, ContactId)` y `MessageEvent(Wamid, Estado)`; `MessageEvent(AttemptId)` para correlación.
- Verificación: `docker compose ... ps` → `Up (healthy)`; `dotnet ef database update` → `Done.`; `verificar-f0.4.sql` → 4 tablas e `IX_SendAttempt_Wamid` con filtro `([Wamid] IS NOT NULL)`; el mismo comando probado desde cmd; `dotnet build` 0 advertencias; `dotnet test` 5/5 (3 nuevas sobre el modelo, incluida la regla 11 aplicada a todos los índices únicos); git grep `EAA…` y `Password=` sin resultados en el repo.
- Fuentes: https://learn.microsoft.com/sql/linux/quickstart-install-connect-docker (imagen, `MSSQL_SA_PASSWORD`, `mssql-tools18` y `-C`); https://learn.microsoft.com/ef/core/modeling/indexes (`HasFilter`).
- Archivos: `deploy/docker/compose.dev.yml`, `src/backend/WaBulk.Domain/Messaging/*`, `src/backend/WaBulk.Infrastructure/Persistence/**`, `src/backend/WaBulk.Api/Program.cs`, `src/backend/WaBulk.Tests/PersistenceModelTests.cs`, `db/scripts/verificar-f0.4.sql`, `dotnet-tools.json`, `CLAUDE.md`.
- Decisiones: D-017 (confirma D-013; puerto 14333; cadena de conexión en user-secrets).
- Pendientes: FK a CampaignVersion/Contact (Fase 1); índices de reclamo (F0.9) e inbox (F0.7).
- Siguiente: F0.5.

### 2026-11-16 — F0.3 Esqueleto de la solución .NET (Claude Code)
- Hecho: `src/backend/WaBulk.sln` con Domain, Infrastructure (classlib), Api y Webhooks (web mínimo con `/health` vía `AddHealthChecks`), Worker (worker service, marcador hasta F0.9) y Tests (xUnit + `Microsoft.AspNetCore.Mvc.Testing` 10.0.15); referencias Infrastructure→Domain, hosts→Domain+Infrastructure, Tests→todos. `Directory.Build.props` (net10.0, nullable, warnings como errores), `.editorconfig` y `.gitattributes` (LF; CRLF para `.cmd`, `.bat`, `.sln`).
- Verificación: `dotnet --version` → 10.0.404; `dotnet build src/backend/WaBulk.sln` → 0 advertencias, 0 errores; `dotnet test` → 2/2 superadas; `msbuild -getProperty` confirma TreatWarningsAsErrors=true; git grep `EAA…` vacío; `.env` y `appsettings.*.local.json` ignorados.
- Archivos: `src/backend/**`, `.editorconfig`, `.gitattributes`, `CLAUDE.md` (Comandos). Commit 8fb9978.
- Decisiones: ninguna nueva. Nota técnica: `Mvc.Testing` fijado en 10.0.x (la 11.0 es solo net11); las pruebas usan tipos marcador por ensamblado en lugar de `Program`.
- Pendientes: ninguno nuevo.
- Siguiente: F0.4.

### 2026-11-16 — F0.2 iniciada (Claude desktop)
- Estado: repo privado https://github.com/Avillegasa/whatsapp-bulk con commit 8176d15 (solo README) en `main`; rama local `dev`. Clon en `C:\Users\avillegas\dev\whatsapp-bulk`.
- Decisión: D-016 — el clon pasa a ser la carpeta del proyecto.
- Hecho: robocopy al clon (16 archivos, 0 errores); commit `F0.2: setup inicial del proyecto` (bdafc0e) en `main` y push; `dev` sincronizada y publicada.
- Verificación: F0.2 → Verificado.
- Siguiente: F0.3 (Claude Code) en el clon.

### 2026-11-16 — F0.1: portafolio de prueba (Claude desktop)
- Decisión: D-015 — el PoC usa un portafolio propio de prueba; la empresa aún no tiene uno.
- Hecho: app `WaBulk PoC` (App ID 1669296924583840) en el portafolio `WaBulk Preubas`.
- Hecho: número de prueba +1 (555) 189-2694, Phone Number ID 1524289747428097, Messaging account ID 2088365445132575 (anotados en META-SETUP.md).
- Hecho: `hello_world` recibido en el teléfono de Andrés (confirmación de Andrés, 12:09). Sin tokens en archivos (grep `EAA…`).
- Hecho: System User `wabulk-poc` creado con acceso total a la cuenta de WhatsApp 2043065989689955 (captura de Andrés); IDs de portafolio, WABA y System User anotados.
- Hecho: token de `wabulk-poc` generado (whatsapp_business_messaging, whatsapp_business_management; caducidad: nunca) y guardado en el gestor de contraseñas de Andrés. `business_management` no estaba disponible en el panel.
- Verificación: F0.1 → Verificado.
- Siguiente: F0.2 — repositorio en GitHub (la carpeta aún no es repo git).

### 2026-11-16 — F0.1 iniciada (Claude desktop)
- Hecho: instrucciones del proyecto de claude.ai configuradas; guía `guia-del-proyecto.md` en el proyecto; comandos para cmd.
- Quedó en: F0.1 Paso 1 — confirmar acceso de Andrés al portafolio de Meta de la empresa (nombre del portafolio y rol).
- Siguiente: continuar F0.1 desde el Paso 1.

### 2026-11-16 — Setup inicial (Claude desktop)
- Hecho: estructura de carpetas, `CLAUDE.md`, `docs/` (arquitectura, plan, progreso, decisiones, setup de Meta), skills `wa-sesion` y `wa-verificar`, `.gitignore`, `.env.example`.
- Decisiones: D-001 a D-014.
- Verificación: archivos presentes en la carpeta del proyecto.
- Siguiente: F0.1 y F0.2 (Tú), pueden hacerse en paralelo.
