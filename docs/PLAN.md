# Plan de trabajo

Cada tarea tiene un ID, quién la hace, qué entrega y cómo se verifica. Una tarea pasa a **Verificado** solo cuando se cumplen todos sus criterios con evidencia (ver skill `wa-verificar`).

- **Tú:** Andrés, guiado en Claude desktop ("Sigamos con WhatsApp").
- **Claude Code:** en esta carpeta, con "Sigamos con la tarea F0.x".

---

## Fase 0 — PoC (número de prueba de Meta, hasta 5 destinatarios)

Objetivo: probar la cadena completa y los escenarios de falla que comprometen la confiabilidad.

### F0.1 — App de Meta y número de prueba
- **Quién:** Tú
- **Pasos:** `docs/META-SETUP.md`, sección A.
- **Verificación:** recibiste `hello_world` en tu teléfono; App ID, WABA ID y Phone Number ID de prueba anotados en la tabla de `META-SETUP.md`; el token no está en ningún archivo.

### F0.2 — Repositorio en GitHub
- **Quién:** Tú
- **Pasos:** crear el repositorio privado, `git init`, primer commit con este setup, push.
- **Verificación:** `git remote -v` apunta a GitHub; `git status` limpio; el repositorio es privado.

### F0.3 — Esqueleto de la solución .NET
- **Quién:** Claude Code · **Depende de:** F0.2
- **Entregables:** `src/backend/WaBulk.sln` con `WaBulk.Domain`, `WaBulk.Infrastructure`, `WaBulk.Api`, `WaBulk.Webhooks`, `WaBulk.Worker`, `WaBulk.Tests` (xUnit), todos `net10.0`; `Directory.Build.props` (nullable, warnings como errores); `.editorconfig`; endpoint `/health` en Api y Webhooks; sección "Comandos" de `CLAUDE.md` actualizada.
- **Verificación:** `dotnet --version` muestra 10.x; `dotnet build src/backend/WaBulk.sln` sin errores ni warnings; `dotnet test` pasa.

### F0.4 — SQL Server local y migración inicial
- **Quién:** Claude Code · **Depende de:** F0.3
- **Entregables:** `deploy/docker/compose.dev.yml` con SQL Server 2022 (clave desde `.env`); `DbContext` con `WebhookInbox`, `DispatchJob`, `SendAttempt`, `MessageEvent`; migración EF Core inicial; índice único filtrado en `SendAttempt.Wamid`.
- **Verificación:** contenedor `healthy`; `dotnet ef database update` aplica sin errores; consulta a `sys.tables` lista las 4 tablas; consulta a `sys.indexes` muestra el filtro `([Wamid] IS NOT NULL)`.

### F0.5 — Cliente de Meta y envío con SendAttempt
- **Quién:** Claude Code (+ Tú cargas los secretos con `dotnet user-secrets`) · **Depende de:** F0.1, F0.4
- **Entregables:** cliente HTTP tipado (versión de Graph API por configuración, sin retry automático); comando de prueba que envía una plantilla a un número permitido; `SendAttempt` confirmado antes del POST; `biz_opaque_callback_data` = AttemptId; clasificación `Accepted` / `Rejected` / `SubmissionUnknown`.
- **Verificación:** pruebas unitarias con handler HTTP falso para 200, error 131026 y timeout (→ `SubmissionUnknown`); envío real de `hello_world` a tu número con `SendAttempt` en `Accepted` y `wamid` guardado.

### F0.6 — Receptor de webhooks
- **Quién:** Claude Code + Tú (túnel y configuración en Meta, `META-SETUP.md` sección B) · **Depende de:** F0.4
- **Entregables:** GET de verificación; POST con validación de `X-Hub-Signature-256` sobre bytes crudos; límite de tamaño; guardado en `WebhookInbox` antes del 200.
- **Verificación:** pruebas: firma válida → 200 y fila en inbox; inválida o ausente → 401 y sin fila; "Verify and save" en Meta exitoso; un webhook de prueba enviado desde el panel de Meta (botón Probar de un campo) guardado en `WebhookInbox` (D-020: sin publicar la app no llegan webhooks reales).

### F0.7 — Procesador de eventos
- **Quién:** Claude Code · **Depende de:** F0.5, F0.6
- **Entregables:** procesador del inbox; separación de lotes; correlación por `wamid` y por AttemptId; `MessageEvent` idempotente; estados que solo avanzan; estados `EnEspera` y `Revision` en el inbox.
- **Verificación:** pruebas: evento duplicado no duplica; `read` antes de `delivered` no retrocede; webhook llegado antes de guardar el `wamid` se correlaciona por AttemptId; secuencia `sent` → `delivered` → `read` reproducida con payloads de replay basados en la referencia oficial (D-020; la prueba con estados reales pasa a F1.1).

### F0.8 — Herramienta de replay firmado
- **Quién:** Claude Code · **Depende de:** F0.6
- **Entregables:** `tools/webhook-replay`: firma payloads JSON de ejemplo con el App Secret y los envía al receptor local; payloads de duplicado, desorden, lote, `user_preferences` stop y plantilla `PAUSED`.
- **Verificación:** cada escenario se reproduce con un solo comando y queda documentado en `tools/webhook-replay/README.md`.

### F0.9 — Despachador con cola y lease
- **Quién:** Claude Code · **Depende de:** F0.5, F0.7
- **Entregables:** reclamo de lote con `UPDLOCK, READPAST` y lease; verificación de dueño del lease; recuperación de leases vencidos.
- **Verificación:** pruebas de integración: caída antes del POST → el trabajo vuelve a la cola; caída después del POST → `SubmissionUnknown`/`NeedsReview` y el handler falso registra un solo POST.

### F0.10 — Bajas
- **Quién:** Claude Code · **Depende de:** F0.9
- **Entregables:** `ConsentRecord`, `PlatformPreference`; detección de botón y palabras clave; `user_preferences`; revisión al encolar y al enviar.
- **Verificación:** tras cada tipo de baja, el siguiente trabajo de ese contacto queda `Skipped` sin POST; un `resume` no levanta una baja de la empresa.

### F0.11 — Concurrencia y capacidad
- **Quién:** Claude Code · **Depende de:** F0.9
- **Entregables:** `CapacityReservation` atómica por portafolio; cupo manual configurable; dos despachadores simultáneos.
- **Verificación:** prueba con 2 despachadores y presupuesto de 10: como máximo 10 reservas y ningún trabajo procesado dos veces.

### F0.12 — Alertas simuladas
- **Quién:** Claude Code · **Depende de:** F0.7, F0.9
- **Entregables:** tabla de alertas y log; pausa global ante 190, 131042 y plantilla `PAUSED`.
- **Verificación:** prueba por cada caso: la campaña o el sistema queda en pausa y se registra la alerta.

### Puerta de la Fase 0
Todos los anteriores en **Verificado**. No se exige `Read` en todos los mensajes. El entorno de prueba no mide capacidad ni facturación de producción.

---

## Fase 1 — Pruebas internas (número de la empresa, solo personal con consentimiento)

Las tareas se detallan (entregables y verificación) al terminar la Fase 0.

- **F1.1** Tú: verificación del negocio, número dedicado, registro y método de pago (`META-SETUP.md` sección C).
- **F1.2** Esqueleto Angular 21 y login.
- **F1.3** Usuarios propios: ASP.NET Core Identity, roles, auditoría.
- **F1.4** Contactos, importación CSV/Excel y consentimiento.
- **F1.5** Plantillas: sincronización con Meta y webhooks de plantilla.
- **F1.6** Campañas versionadas, aprobación de cuatro ojos y vencimiento.
- **F1.7** Despacho completo: tasa, capacidad, cupo manual, fatiga, freno de emergencia, horario.
- **F1.8** Monitor en vivo (SignalR) y tablero.
- **F1.9** Docker: Dockerfiles, compose de producción, secretos por entorno.
- **F1.10** Campañas a 20–50 empleados y conciliación de `pricing` contra facturación.
- **Puerta F1:** cero duplicados, bajas aplicadas en menos de 1 minuto, calidad en verde.

## Fase 2 — Producción gradual

- **F2.1** Condiciones antes del piloto (documento de diseño, sección 5).
- **F2.2** Primera campaña de promociones a 200–500 clientes; observación 48 h.
- **F2.3** Escalones hasta el límite disponible.
- **F2.4** (Futuro) Inicio de sesión con Microsoft Entra ID.
- **F2.5** (Futuro) Plantillas de utility: avisos de pedido y factura emitida.
