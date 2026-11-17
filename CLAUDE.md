# WhatsApp Bulk — Envíos masivos por WhatsApp Cloud API

Sistema interno de la empresa para enviar promociones por WhatsApp a clientes con consentimiento, usando la API oficial de Meta (Cloud API). Sin BSP, sin WhatsApp Web, sin extensiones.

## Antes de cualquier tarea (obligatorio)

1. Lee `docs/PROGRESO.md` (estado, siguiente tarea, última sesión) y la tarea en `docs/PLAN.md`.
2. Sigue la skill `wa-sesion` (inicio, trabajo y cierre de sesión).
3. Trabaja solo en la tarea indicada. Lo que surja fuera de alcance va a "Pendientes" en `docs/PROGRESO.md`.
4. Una tarea no está **Verificado** sin evidencia (salida de comando, prueba o confirmación de Andrés). Usa la skill `wa-verificar`.

## Fuentes de verdad

| Archivo | Contenido |
| --- | --- |
| `docs/ARQUITECTURA.md` | Componentes, flujo, modelo de datos, estados, errores, reglas de bajas y capacidad |
| `docs/PLAN.md` | Tareas por fase con quién las hace y cómo se verifican |
| `docs/PROGRESO.md` | Estado de cada tarea y bitácora |
| `docs/DECISIONES.md` | Decisiones tomadas (D-001…) y su motivo |
| `docs/META-SETUP.md` | Configuración en Meta e IDs no secretos |

Documento de diseño completo (Claude Docs): https://claude.ai/artifact/4QbfsuRFZo1uKJcBwJaLgu

## Stack

- Frontend: Angular 21 (standalone, signals) en `src/frontend/`.
- Backend: .NET 10 (ASP.NET Core, Worker Service) en `src/backend/`.
- Base de datos: SQL Server (2022 en Docker para desarrollo). También es la cola.
- Autenticación: usuarios propios (ASP.NET Core Identity, cookie) con roles Operador, Aprobador, Admin. Diseñar para agregar Microsoft Entra ID más adelante sin rehacer la autorización.
- Despliegue: contenedores Docker (Api, Webhooks, Worker y frontend con nginx).
- Repositorio: GitHub (privado).

## Estructura

```
src/backend/    Solución WaBulk.sln (Domain, Infrastructure, Api, Webhooks, Worker, Tests)
src/frontend/   App Angular (desde la Fase 1)
db/scripts/     Scripts SQL de apoyo (consultas de verificación, datos de prueba)
deploy/docker/  Dockerfiles y compose (dev y prod)
tools/          Utilidades de desarrollo (replay de webhooks firmados)
docs/           Plan, progreso, decisiones, arquitectura, setup de Meta
.claude/skills/ Skills de este proyecto (wa-sesion, wa-verificar)
```

## Reglas que no se rompen

1. **Verificar antes de asumir.** Endpoints, payloads, códigos de error y sintaxis de Meta, Microsoft o Angular se confirman en la documentación oficial antes de escribir código. Anota la URL en un comentario o en la bitácora cuando la decisión no sea obvia.
2. **Versión de Graph API configurable** (`Meta:GraphApiVersion`), nunca literal en el código.
3. **Anti-duplicados:** se inserta y confirma `SendAttempt` antes del POST; el POST lleva `biz_opaque_callback_data` = AttemptId; sin política de reintento automático genérica (Polly/Resilience) sobre `/messages`.
4. **Sin respuesta confiable → `SubmissionUnknown`**, nunca reenvío automático.
5. **Cola:** reclamo en transacción corta con lease (`LockedBy`, `LeaseUntilUtc`); nunca mantener bloqueos durante una llamada HTTP; cada actualización verifica que el worker sigue siendo dueño del lease.
6. **Webhooks:** validar `X-Hub-Signature-256` sobre los bytes crudos con comparación de tiempo constante; guardar en `WebhookInbox` y recién responder 200; procesar fuera de la petición; idempotente.
7. **Estados:** despacho y entrega son estados separados; el de entrega solo avanza.
8. **Bajas:** consentimiento de la empresa y preferencias de plataforma son capas separadas; se revisan al encolar y al enviar; un `resume` de plataforma nunca borra una baja de la empresa.
9. **Reintentos** solo para códigos transitorios (4, 80007, 130429, 131056, 131000, 131016), con backoff y jitter, máximo 5, nunca después de `ExpiraUtc`.
10. **Secretos** (token, App Secret, verify token, PIN) nunca en el repositorio: `dotnet user-secrets` en desarrollo, variables de entorno o `.env` (ignorado por git) en Docker.
11. **Índices únicos** sobre columnas que admiten NULL siempre filtrados (`WHERE col IS NOT NULL`).
12. **Alcance:** solo promociones (marketing) en la primera etapa. Mensajes de cobranza o saldos vencidos: nunca.

## Comandos

Mantener esta sección al día. Todos funcionan en cmd desde la raíz del repo.

- SDK: `dotnet --version` (10.x)
- Backend: `dotnet build src/backend/WaBulk.sln` · `dotnet test src/backend/WaBulk.sln` (las pruebas de integración de la cola necesitan SQL Server levantado: crean y borran una base `WaBulk_Test_<guid>`; sin SQL Server: `dotnet test src/backend/WaBulk.sln --filter Category!=Integration`)
- Ejecutar Api: `dotnet run --project src/backend/WaBulk.Api` → `GET /health` responde `Healthy`
- Ejecutar Webhooks: `dotnet run --project src/backend/WaBulk.Webhooks` → `http://localhost:5244`, `GET /health`; receptor en `/webhooks/whatsapp` (sin `Meta:AppSecret`/`Meta:VerifyToken` responde 503)
- Túnel para webhooks (otra ventana, ya creado): `devtunnel host wabulk-k7m2p9x4` → URL `https://…-5244…devtunnels.ms`; apagar con Ctrl+C al terminar (META-SETUP.md sección B)
- Ejecutar Worker: `dotnet run --project src/backend/WaBulk.Worker` (procesa `WebhookInbox` cada 2 s, sección `InboxProcessor`). El despachador está apagado por defecto (D-024); encenderlo en cmd: `set Dispatcher__Enabled=true` antes del `dotnet run` (envía de verdad lo que haya en cola)
- Ver bajas (`ConsentRecord`, `PlatformPreference`, estado vigente y trabajos `Skipped`): ver comando en `db/scripts/verificar-f0.10.sql`
- Ver capacidad (destinatarios únicos vigentes, reservas, trabajos pospuestos): ver comando en `db/scripts/verificar-f0.11.sql`
- Alertas y pausa global: `dotnet run --project src\backend\WaBulk.Worker -- alertas` (estado y alertas abiertas) · `dotnet run --project src\backend\WaBulk.Worker -- reanudar` (levanta la pausa; sin argumentos, registra el usuario de Windows en `LevantadaPor`); historial en `db/scripts/verificar-f0.12.sql`
- Ver la cola (`DispatchJob`, leases, índices): ver comando en `db/scripts/verificar-f0.9.sql`. `sqlcmd` lleva `-I` para poder escribir en `DispatchJob` (índices filtrados)
- Replay de webhooks firmados (receptor y Worker corriendo): `dotnet run --project tools\webhook-replay -- lista` · `dotnet run --project tools\webhook-replay -- desorden --intento ultimo` → resultado con `db/scripts/verificar-f0.8.sql`; escenarios en `tools/webhook-replay/README.md`
- Herramientas locales (dotnet-ef): `dotnet tool restore`
- SQL Server local: `docker compose -f deploy/docker/compose.dev.yml --env-file .env up -d sqlserver` (expuesto en `127.0.0.1,14333`; clave en `.env`)
- Estado del contenedor: `docker compose -f deploy/docker/compose.dev.yml --env-file .env ps`
- Usar el token en cmd sin mostrarlo (nunca pegar secretos en el chat; si se pegan, rotarlos):
  `for /f "tokens=2 delims== " %A in ('dotnet user-secrets list --project src\backend\WaBulk.Worker ^| findstr /b /c:"Meta:AccessToken"') do @set "WA_TOKEN=%A"` → usar `%WA_TOKEN%` → `set WA_TOKEN=`
- En los comandos de ejemplo, `<valor>` es un marcador: en cmd no se escriben `<` ni `>` (son redirecciones).
- Secretos de desarrollo: un solo almacén de user-secrets compartido por Api, Webhooks y Worker (D-018); `--project` puede ser cualquiera de ellos.
  - Cadena de conexión: `dotnet user-secrets set "ConnectionStrings:WaBulk" "Server=127.0.0.1,14333;Database=WaBulk;User Id=sa;Password=<clave de .env>;Encrypt=True;TrustServerCertificate=True" --project src/backend/WaBulk.Api`
  - Token de Meta: `dotnet user-secrets set "Meta:AccessToken" "<token>" --project src/backend/WaBulk.Worker`
  - Webhooks: `dotnet user-secrets set "Meta:AppSecret" "<app secret>" --project src/backend/WaBulk.Webhooks` · `dotnet user-secrets set "Meta:VerifyToken" "<verify token>" --project src/backend/WaBulk.Webhooks`
- Envío de prueba (plantilla a un número permitido): `dotnet run --project src\backend\WaBulk.Worker -- send-test --to +591XXXXXXXX [--template hello_world] [--lang en_US]`
- Nueva migración: `dotnet ef migrations add <Nombre> --project src/backend/WaBulk.Infrastructure --startup-project src/backend/WaBulk.Api --output-dir Persistence/Migrations`
- Aplicar migraciones: `dotnet ef database update --project src/backend/WaBulk.Infrastructure --startup-project src/backend/WaBulk.Api`
- Verificar tablas e índices: ver comando en `db/scripts/verificar-f0.4.sql`
- Ver webhooks guardados en `WebhookInbox`: ver comando en `db/scripts/verificar-f0.6.sql`
- Ver inbox procesado, `MessageEvent` y `EstadoEntrega`: ver comando en `db/scripts/verificar-f0.7.sql`
- Primer Admin (una sola vez; la clave se pide sin mostrarla): `dotnet run --project src\backend\WaBulk.Api -- crear-admin --usuario <nombre>`
- Ver usuarios (roles, activo, bloqueo) y las últimas filas de `AuditLog`: ver comando en `db/scripts/verificar-f1.3.sql`
- Ver contactos, mensajes entrantes, ofertas y consentimiento vigente: ver comando en `db/scripts/verificar-f1.4.sql`
- Pantallas de contactos (Mensajes recientes, Contactos, Importar, Listas): solo con rol Operador; la oferta necesita el Worker corriendo (procesa la respuesta) y el receptor con el túnel
- Frontend: `cd src\frontend && npm ci` · `npx ng build` · `npx ng test --watch=false` (Vitest)
- Intranet en desarrollo (dos ventanas): `dotnet run --project src\backend\WaBulk.Api --launch-profile http` (http://localhost:5047) y `cd src\frontend && npx ng serve` → http://localhost:4200 (el proxy manda `/api` a la Api)

Notas de la solución:
- `src/backend/Directory.Build.props` fija `net10.0`, nullable y warnings como errores para todos los proyectos.
- Paquetes `Microsoft.AspNetCore.*` / `Microsoft.Extensions.*` en la línea 10.0.x (las 11.x apuntan a net11).
- Las pruebas de integración usan `WebApplicationFactory<ApiAssemblyMarker>` / `<WebhooksAssemblyMarker>` (no `Program`, que se repite en Api y Webhooks).
- Datos: entidades en `WaBulk.Domain/Messaging`, `WaBulkDbContext` y configuraciones en `WaBulk.Infrastructure/Persistence`. Propiedades en inglés, columnas con los nombres de `docs/ARQUITECTURA.md` (`HasColumnName`); enums guardados como texto.
- Meta: `MetaWhatsAppClient` (Infrastructure/Meta) clasifica cada POST en Accepted/Rejected/SubmissionUnknown, sin retry; `TemplateSendService` (Infrastructure/Messaging) confirma el `SendAttempt` antes del POST. IDs y `GraphApiVersion` en `appsettings.json` del Worker; secretos solo en user-secrets.
- Cola: `DispatchQueue` (Infrastructure/Messaging, SQL crudo con `UPDLOCK, READPAST`) reclama, verifica el lease y recupera leases vencidos; `Dispatcher` hace una vuelta y `DispatcherWorker` la repite. La regla de reintentos (regla 9) está en `WaBulk.Domain/Messaging/DispatchRetryPolicy.cs`.
- Capacidad: `CapacityService` (Infrastructure/Capacity) reserva con `sp_getapplock` por portafolio; presupuesto = `Capacity:PortfolioLimit` × `UsagePercent` − `ManualQuota` (`CapacityBudget` en Domain). Sin lugar, el trabajo queda `RetryScheduled` sin sumar intento.
- Freno de emergencia: `AlertService` (Infrastructure/Alerts) registra `Alert` + `DispatchPause` global (190, 131042, 132015 al enviar; `PAUSED` y 131042 por webhook); con pausa activa el despachador no reclama. Códigos en `WaBulk.Domain/Alerts/Alert.cs` (`EmergencyBrake`).
- Bajas: entidades y reglas en `WaBulk.Domain/Consent` (`MarketingEligibility`, `OptOutDetector`); `ConsentService` y `EnqueueService` en `WaBulk.Infrastructure/Consent`. Sin OptIn vigente no se envía (D-025). Palabras clave y botón de baja en la sección `Bajas` del Worker.
- Usuarios (F1.2): `WaBulkDbContext` hereda de `IdentityDbContext<AppUser>` (tablas `AspNet*` en la misma base; roles como datos de la migración). La Api usa cookie `WaBulk.Auth` (401/403 sin redirección), política por defecto "con sesión" y `XsrfValidationFilter` en todo POST/PUT/PATCH/DELETE de `/api` (cookie `XSRF-TOKEN` + encabezado `X-XSRF-TOKEN`). Endpoints con políticas con nombre (`WaBulk.Domain/Users/Roles.cs`), no con nombres de rol; `EndpointPolicyTests` falla si un endpoint nuevo no tiene política.
- Usuarios y auditoría (F1.3, D-030): `UserAdminService` (Infrastructure/Identity) hace cada cambio y su fila de `AuditLog` en una sola transacción (`AuditEntry.Create`); usuarios desactivados con `Activo` (no se borran), revalidados en cada petición por `AppSignInManager`. Toda acción nueva que cambie datos de negocio debe auditar igual. Actor de los comandos de consola: `windows:<usuario>`.
- Contactos (F1.4, D-031, D-032): `Contact`/`InboundMessage`/`ConsentOffer`/`Segment` en `WaBulk.Domain/Contacts`; servicios en `WaBulk.Infrastructure/Contacts` (`ContactDirectory` mapea BSUID ↔ teléfono, `ConsentOfferService` revisa la ventana y hace un solo POST de botones tras guardar la oferta, `ContactImportService` CSV/Excel con ClosedXML). Texto de la oferta en la sección `OfertaPromociones` de la Api. La Api también envía por Meta (`AddMetaWhatsApp`): mismo token en user-secrets. Tras cambiar el procesador, reiniciar el Worker.
- Webhooks: el receptor solo guarda; `WebhookInboxProcessor` (Infrastructure/Messaging, corre en el Worker) correlaciona estados y escribe `MessageEvent` y `SendAttempt.EstadoEntrega` (solo avanza).
- Si `dotnet build` falla con "archivo bloqueado por WaBulk.Webhooks", el receptor está corriendo: detenerlo con Ctrl+C o compilar con `-o` a otra carpeta.

## Estilo

- Identificadores de código en inglés; tablas y columnas con los nombres de `docs/ARQUITECTURA.md`.
- Documentación, comentarios y mensajes de commit en español.
- Commits pequeños con prefijo de tarea: `F0.5: cliente Meta y SendAttempt`.
- Andrés usa **cmd** (Símbolo del sistema), no PowerShell: todo comando para él debe funcionar en cmd (continuación de línea con `^`, variables con `%VAR%`).
- Andrés está aprendiendo el dominio: al cerrar cada tarea explica en 2–4 líneas qué se hizo y por qué, con un ejemplo si ayuda.
