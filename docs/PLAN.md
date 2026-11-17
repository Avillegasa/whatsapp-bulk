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
- **Verificación:** pruebas: evento duplicado no duplica; `read` antes de `delivered` no retrocede; webhook llegado antes de guardar el `wamid` se correlaciona por AttemptId; secuencia `sent` → `delivered` → `read` reproducida con payloads de replay basados en la referencia oficial (D-020); además, un envío real con el túnel activo deja `MessageEvent` `sent` y `delivered` correlacionados por `wamid` o AttemptId aunque lleguen en desorden (D-021). Respuestas entrantes y `user_preferences` reales: F1.1.

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

Objetivo: el sistema completo, con el número real de la empresa, probado solo con empleados que dieron su consentimiento.

Orden: F1.1 depende de terceros (verificación del negocio, puede tardar días); se empieza primero y en paralelo con F1.2–F1.4. Las verificaciones "reales" de F1.5, F1.7 y F1.10 necesitan F1.1.

### F1.1 — Activos de Meta de la empresa
- **Quién:** Tú (+ Claude Code cambia IDs en configuración) · **Depende de:** portafolio de Meta de la empresa creado (D-015); número dedicado que no esté activo en la app de WhatsApp
- **Pasos:** `docs/META-SETUP.md` sección C, uno a la vez en Claude desktop, confirmando cada paso en la documentación de Meta.
- **Entregables:** negocio verificado; app de la empresa (la PoC no se reutiliza, D-022); WABA y número de la empresa con nombre visible aprobado; número registrado con `POST /{PHONE_NUMBER_ID}/register` y PIN de 6 dígitos (máximo 10 registros en 72 h, si no error 133016); método de pago; System User con token sin caducidad; webhooks suscritos con la WABA real (repite B.4–B.6); IDs en `META-SETUP.md`; `appsettings` con los IDs nuevos (`Meta:*`, `Capacity:PortfolioId`); token y PIN solo en el gestor de contraseñas y user-secrets.
- **Verificación:** `GET /{PHONE_NUMBER_ID}?fields=whatsapp_business_manager_messaging_limit,throughput,quality_rating` responde con el límite y calidad `GREEN`; `send-test` desde el número de la empresa a tu teléfono → `Accepted` y estados `sent`/`delivered` en `WebhookInbox`; tu respuesta "Hola" llega como mensaje entrante; "BAJA" escrita por ti queda como `ConsentRecord` OptOut (y luego se revierte con opt-in manual); git grep sin `EAA…`.

### F1.2 — Login y esqueleto Angular
- **Quién:** Claude Code · **Depende de:** F0.3
- **Entregables:** ASP.NET Core Identity con cookie en `WaBulk.Api` (tablas Identity en la misma base); roles Operador, Aprobador, Admin; bloqueo tras intentos fallidos; protección XSRF (cookie + encabezado que Angular envía); comando para crear el primer Admin sin clave en el repo; app Angular 21 en `src/frontend` (standalone, signals) con pantalla de login, cierre de sesión, guardia de rutas y menú por rol.
- **Verificación:** pruebas: sin sesión → 401; rol incorrecto → 403; 5 claves malas → bloqueado; POST sin token XSRF → rechazado; `npx ng build` y `npx ng test` pasan; login real en el navegador.

### F1.3 — Usuarios, autorización y auditoría
- **Quién:** Claude Code · **Depende de:** F1.2
- **Entregables:** pantalla de usuarios (Admin: crear, desactivar, cambiar rol, restablecer clave); autorización por políticas con nombre (`PuedeAprobar`, `PuedeEnviar`…) y no por nombre de rol en los endpoints, para sumar Entra ID después sin rehacerla; `AuditLog` (Usuario, Accion, Entidad, Antes, Despues, FechaUtc) escrito en la misma transacción que el cambio; `reanudar` del Worker también audita.
- **Verificación:** pruebas: cada endpoint protegido por una política; cambio de rol → fila en `AuditLog` con antes/después; usuario desactivado no inicia sesión ni conserva su sesión abierta.

### F1.4 — Contactos, importación y consentimiento
- **Quién:** Claude Code · **Depende de:** F1.3
- **Entregables:** `Contact` (PhoneE164 único, WaId, UserId/BSUID, Nombre, ClienteId, Activo); FK de `ConsentRecord.ContactId` y `DispatchJob.ContactId`; mapeo BSUID ↔ teléfono (las bajas sin teléfono que hoy van a `Revision` se resuelven si hay mapeo); importación CSV/Excel con vista previa, validación E.164 (591 + 8 dígitos) y reporte por fila; opt-in con evidencia obligatoria (fuente, fecha, documento o detalle); baja y opt-in manual desde la intranet (`Fuente` Intranet, auditados); listas estáticas (`Segment`, `SegmentMember`).
- **Verificación:** pruebas: 100 filas con 5 inválidas → 95 contactos y 5 errores con su motivo; teléfono repetido → un contacto; importación con OptIn sobre un OptOut vigente → sigue OptOut; opt-in sin evidencia → rechazado; baja desde la intranet → el siguiente trabajo `Skipped`; todo en `AuditLog`.
- **Antes de empezar:** confirmar con el área comercial o legal cómo consta el consentimiento (define los campos de evidencia).

### F1.5 — Plantillas
- **Quién:** Claude Code (+ Tú creas y pruebas la plantilla real) · **Depende de:** F1.3; verificación real con F1.1
- **Entregables:** `Template` sincronizada con `GET /{WABA_ID}/message_templates`; creación de plantillas de marketing (D-009) desde la intranet con parámetros y botones "Me interesa" (D-012) y "No recibir más"; webhooks `message_template_status_update`, `message_template_quality_update` y `template_category_update` actualizan estado, calidad y categoría; el envío de prueba usa la plantilla y sus `components`.
- **Verificación:** pruebas con payloads de replay por cada webhook; real: plantilla creada en la WABA de la empresa → `APPROVED` llega por webhook y se ve en la pantalla; tocar "No recibir más" en tu teléfono → baja registrada y payload real anotado en `Bajas:ButtonPayloads`/`ButtonTexts` (cierra el pendiente de D-025).

### F1.6 — Campañas versionadas y aprobación
- **Quién:** Claude Code · **Depende de:** F1.4, F1.5
- **Entregables:** `Campaign` y `CampaignVersion` inmutable (plantilla, idioma, categoría, audiencia congelada, parámetros, `ProgramadaUtc`, `ExpiraUtc`, Hash); estados de campaña de ARQUITECTURA; aprobación de cuatro ojos (quien aprueba ≠ quien creó, validado en el backend); al aprobar, un `DispatchJob` por destinatario en una sola transacción (único `CampaignVersionId, ContactId`; FK `CampaignVersionId`); pausa, reanudación y cancelación por campaña; el despachador toma plantilla y parámetros de la versión (se retira `Dispatcher:TemplateName`, D-024).
- **Verificación:** pruebas: el creador no puede aprobar (403); editar una versión aprobada → versión nueva que exige aprobación; dos aprobaciones simultáneas → los trabajos se crean una sola vez; vencida → `Expired` sin POST; cancelada → lo pendiente no sale.

### F1.7 — Despacho completo
- **Quién:** Claude Code · **Depende de:** F1.6
- **Entregables:** tasa por número (token bucket, 10–20 msg/s configurable); límite real de Meta (campo `whatsapp_business_manager_messaging_limit` y webhook `business_capability_update`, D-026); mensajes entrantes guardados y envíos dentro de la ventana de 24 h descontados de la capacidad; fatiga (D-012) y 131049 (+24 h, un intento más); horario permitido (08:00–20:00 America/La_Paz); revalidación al enviar de campaña, vencimiento y plantilla `APPROVED`; resto del freno de emergencia (`phone_number_quality_update`, 131048/131031/368, fallos > 5 % en los últimos 200, 132000–132016 → pausa de campaña); aviso fuera del log (correo o Teams, a decidir); `NeedsReview` vencido (2 h) listado para revisión.
- **Verificación:** una prueba por control (tasa medida, fuera de horario no envía, 3 promociones sin interacción → contacto pausado, 6 fallos de 100 → pausa, 132001 → pausa solo de esa campaña); real: campaña a 3 empleados respeta tasa y horario.

### F1.8 — Monitor en vivo y tablero
- **Quién:** Claude Code · **Depende de:** F1.7
- **Entregables:** SignalR con contadores por campaña (en cola, enviados, entregados, leídos, fallidos, omitidos y su motivo); pantalla de alertas con "reanudar" auditado; pantalla de `NeedsReview` (marcar "llegó" o "no llegó", nunca reenvío automático); pantalla de bajas; consumo de capacidad del día.
- **Verificación:** con un replay, el contador cambia en el navegador en menos de 5 s; reanudar desde la pantalla → pausa levantada y fila en `AuditLog`.

### F1.9 — Docker y despliegue
- **Quién:** Claude Code + Tú · **Depende de:** F1.2; decisión pendiente D-006 (dónde corre SQL Server en producción) y dónde se publica el receptor con HTTPS
- **Entregables:** Dockerfiles de Api, Webhooks, Worker y frontend (nginx); `deploy/docker/compose.prod.yml`; secretos por variables de entorno o `.env` ignorado; health checks; migraciones aplicadas por un paso explícito (no al arrancar); solo el receptor expuesto a internet.
- **Verificación:** desde cero, `docker compose … up -d` → todos `healthy`; webhook real llega al receptor publicado; `docker history` e imágenes sin secretos; git grep sin `EAA…`.

### F1.10 — Campañas internas y conciliación
- **Quién:** Tú + Claude Code · **Depende de:** F1.1–F1.9
- **Entregables:** opt-in documentado de 20–50 empleados; 2–3 campañas reales; conciliación de `pricing` de `MessageEvent` contra la facturación de Meta (WhatsApp Manager).
- **Verificación:** la puerta de la Fase 1.

### Puerta de la Fase 1
- Cero duplicados: ninguna consulta encuentra dos intentos `Accepted` para el mismo (versión de campaña, contacto).
- Bajas aplicadas en menos de 1 minuto: medido entre el webhook de baja y el trabajo `Skipped`.
- Calidad del número en `GREEN` al terminar; conciliación con diferencias explicadas.
- Todas las tareas F1 en **Verificado**.

---

## Fase 2 — Producción gradual

- **F2.1** Condiciones antes del piloto (documento de diseño, sección 5).
- **F2.2** Primera campaña de promociones a 200–500 clientes; observación 48 h.
- **F2.3** Escalones hasta el límite disponible.
- **F2.4** (Futuro) Inicio de sesión con Microsoft Entra ID.
- **F2.5** (Futuro) Plantillas de utility: avisos de pedido y factura emitida.
