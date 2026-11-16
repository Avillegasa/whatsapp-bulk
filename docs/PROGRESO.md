# Progreso

**Fase actual:** Fase 0 — PoC
**Siguiente tarea:** F0.5 (Claude Code + Tú) — en `C:\Users\avillegas\dev\whatsapp-bulk`: "Sigamos con la tarea F0.5" (ten a mano el token del System User para cargarlo con `dotnet user-secrets`)
**Última actualización:** 2026-11-16

## Estado de tareas

Estados: Pendiente · En curso · Hecho (sin verificar) · Verificado · Bloqueado

| ID | Tarea | Quién | Estado | Evidencia |
| --- | --- | --- | --- | --- |
| F0.1 | App de Meta y número de prueba | Tú | Verificado | `hello_world` recibido (confirmación de Andrés); App, WABA, Phone Number y System User IDs en META-SETUP.md; token del System User (sin caducidad) en gestor de contraseñas; grep `EAA…` sin resultados en la carpeta |
| F0.2 | Repositorio en GitHub | Tú | Verificado | Repo privado Avillegasa/whatsapp-bulk (confirmación de Andrés); `git push origin main` 8176d15..bdafc0e; `dev` al día con `origin/dev`; `git status` limpio; sin `.env` en el commit |
| F0.3 | Esqueleto de la solución .NET | Claude Code | Verificado | `dotnet --version` 10.0.404; `dotnet build` 0 advertencias, 0 errores; `dotnet test` 2/2 (`/health` en Api y Webhooks → 200 `Healthy`); sin secretos (git grep `EAA…` vacío) |
| F0.4 | SQL Server local y migración inicial | Claude Code | Verificado | Contenedor `wabulk-sqlserver` `Up (healthy)`; `dotnet ef database update` → `Done.` (InitialCreate, EF 10.0.15); `sys.tables` → DispatchJob, MessageEvent, SendAttempt, WebhookInbox; `sys.indexes` → `IX_SendAttempt_Wamid` filtro `([Wamid] IS NOT NULL)`; `dotnet test` 5/5; build 0 advertencias |
| F0.5 | Cliente de Meta y envío con SendAttempt | Claude Code + Tú | Pendiente | |
| F0.6 | Receptor de webhooks | Claude Code + Tú | Pendiente | |
| F0.7 | Procesador de eventos | Claude Code | Pendiente | |
| F0.8 | Herramienta de replay firmado | Claude Code | Pendiente | |
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
- Índice de reclamo de la cola en `DispatchJob` (EstadoDespacho, ProximoIntentoUtc/ProgramadoUtc) en F0.9; índice por `Estado` en `WebhookInbox` en F0.7.

## Bitácora (lo más reciente arriba)

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
