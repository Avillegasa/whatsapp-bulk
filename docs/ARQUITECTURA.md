# Arquitectura — resumen operativo

Resumen del documento de diseño para trabajar día a día. Ante una duda de diseño, este archivo y `CLAUDE.md` mandan; si algo falta, consultar el documento completo y registrar la decisión en `DECISIONES.md`.

## Componentes

| Componente | Proyecto | Responsabilidad |
| --- | --- | --- |
| Frontend | `src/frontend` (Angular 21) | Contactos, plantillas, campañas, aprobación, monitor, bajas, auditoría |
| API | `WaBulk.Api` | Reglas de negocio, usuarios y roles, sincronización de plantillas, materialización de la cola |
| Receptor de webhooks | `WaBulk.Webhooks` | Único componente público. Handshake GET, firma, guardar en `WebhookInbox`, responder 200 |
| Worker | `WaBulk.Worker` | Dos `BackgroundService`: Despachador (envía) y Procesador (aplica eventos del inbox) |
| Dominio | `WaBulk.Domain` | Entidades, estados, clasificación de errores, reglas de elegibilidad |
| Infraestructura | `WaBulk.Infrastructure` | EF Core + SQL crudo para la cola, cliente Meta tipado, secretos |
| SQL Server | — | Datos de negocio, cola (`DispatchJob`) e inbox |

Angular nunca habla con Meta. La API nunca envía en masa de forma síncrona. Todo envío pasa por la cola.

## Flujo de un envío

1. Plantilla creada en Meta (`POST /{WABA_ID}/message_templates`) y aprobada (webhook `message_template_status_update`).
2. Campaña: versión inmutable (plantilla, idioma, categoría, audiencia, parámetros, programación, vencimiento) aprobada por alguien distinto del operador. Al aprobar se crea un `DispatchJob` por destinatario.
3. Despachador: reclama lote (transacción corta + lease) → revalida (campaña, vencimiento, plantilla, consentimiento, preferencias, fatiga, capacidad) → inserta `SendAttempt` → POST `/{PHONE_NUMBER_ID}/messages` con `biz_opaque_callback_data` = AttemptId → clasifica resultado.
4. Receptor: valida firma → `WebhookInbox` → 200.
5. Procesador: separa `entry`/`changes`/`statuses`; correlaciona por `wamid` o por AttemptId; aplica eventos idempotentes; actualiza bajas, plantillas, calidad y capacidad; notifica al frontend (SignalR).

## Resultado de un intento

| Respuesta de Meta | Intento | Trabajo |
| --- | --- | --- |
| 200 con `messages[0].id` | `Accepted` (guarda `wamid`) | `Submitted` |
| Error con código | `Rejected` | `RetryScheduled` o `Failed` según código y vencimiento |
| Timeout, conexión cortada, 5xx sin cuerpo, worker caído en `Submitting` | `SubmissionUnknown` | `NeedsReview` (sin reenvío automático) |

`SubmissionUnknown` se resuelve solo si llega un webhook con ese AttemptId; si no, revisión manual tras un plazo configurable (inicial 2 h).

## Estados

- Campaña: `Draft` → `PendingApproval` → `Scheduled` → `Running` → `DispatchCompleted` → `TrackingClosed` (plazo inicial 72 h); laterales `Paused`, `Cancelled`, `Expired`.
- Trabajo (`DispatchJob.EstadoDespacho`): `Queued` → `Claimed` → `Submitted`; laterales `RetryScheduled`, `NeedsReview`, `Failed`, `Skipped`, `Expired`.
- Entrega (por `wamid`): `Accepted` → `Sent` → `Delivered` → `Read`, o `Failed`. Solo avanza.
- Lease vencido: sin intento o con rechazo transitorio → vuelve a la cola; intento en `Submitting` → `SubmissionUnknown`/`NeedsReview`.

## Modelo de datos

| Tabla | Campos clave | Notas |
| --- | --- | --- |
| `Contact` | Id, PhoneE164 (único), WaId, Nombre, ClienteId, Activo | E.164 (Bolivia: 591 + 8 dígitos) |
| `ConsentRecord` | ContactId (opcional hasta Fase 1), PhoneE164, Finalidad, Canal, Estado (OptIn/OptOut/NoContactar), Fuente, Evidencia, WamidOrigen, FechaUtc, RegistradoPor | Historial inmutable; vigente = último por teléfono y finalidad; único filtrado en WamidOrigen (D-025) |
| `PlatformPreference` | PhoneE164, Categoria, Valor (stop/resume), Origen (Webhook/Error131050), FechaUtc | Separada del consentimiento; vigente = FechaUtc de Meta más reciente; único (PhoneE164, Categoria, FechaUtc, Valor) |
| `Segment`, `SegmentMember` | Filtros o listas | Congelados al aprobar |
| `Template` | MetaTemplateId, Nombre, Idioma, Categoria, Estado, Calidad, Componentes (JSON) | Sincronizada con Meta |
| `Campaign`, `CampaignVersion` | Version: plantilla, idioma, categoría, audiencia, parámetros, ProgramadaUtc, ExpiraUtc, Hash, AprobadaPor | Cambios → nueva versión y nueva aprobación |
| `DispatchJob` | Id, CampaignVersionId, ContactId, PhoneE164, Parametros, Prioridad, ProgramadoUtc, ExpiraUtc, EstadoDespacho, Intentos, ProximoIntentoUtc, LockedBy, LeaseUntilUtc, MotivoOmision | Único (CampaignVersionId, ContactId); MotivoOmision solo en `Skipped` (SinConsentimiento, BajaEmpresa, NoContactar, BajaPlataforma) |
| `SendAttempt` | AttemptId, DispatchJobId, InicioUtc, FinUtc, Estado, Wamid, HttpStatus, CodigoError, Detalle, EstadoEntrega | Único filtrado en Wamid; EstadoEntrega solo lo escribe el procesador (D-023) |
| `CapacityReservation` | PortafolioId, PhoneE164, DispatchJobId, ReservadaUtc, ExpiraUtc, Estado (Reservada/Consumida/Liberada) | Una fila por trabajo; cuenta destinatarios únicos vigentes (ventana móvil 24 h); reserva con `sp_getapplock` por portafolio (D-026) |
| `MessageEvent` | Wamid, AttemptId, Estado, TimestampMeta, Pricing, Errores | Único (Wamid, Estado) |
| `WebhookInbox` | Id, RecibidoUtc, Cuerpo, Estado (Pendiente/Procesado/EnEspera/Revision), Intentos, ProximoIntentoUtc, Error | Solo el receptor inserta; el procesador actualiza Estado, Intentos, ProximoIntentoUtc y Error (D-023) |
| `AuditLog` | Usuario, Accion, Entidad, Antes, Despues, FechaUtc | Importaciones, aprobaciones, bajas, reenvíos manuales |

## Controles del despachador

- **Capacidad (portafolio):** límite leído con `GET /{PHONE_NUMBER_ID}?fields=whatsapp_business_manager_messaging_limit` y webhook `business_capability_update`. Reserva atómica por destinatario único fuera de ventana; se consume si Meta acepta, se libera si rechaza. Usar hasta el 90 %.
- **Cupo manual:** 200 destinatarios por 24 h reservados para los envíos manuales que hoy se hacen fuera del sistema (configurable).
- **Tasa por número:** token bucket, inicial 10–20 mensajes/s (techo de Meta: 80).
- **Fatiga por contacto:** 3 promociones seguidas sin interacción (respuesta, botón o lectura) → pausa de promociones 60 días; si la siguiente tampoco tiene interacción → excluido de promociones. Dos 131049 seguidos → misma pausa. Valores configurables.
- **Freno de emergencia:** pausa global ante baja de calidad del número, plantilla `PAUSED`, error de token o facturación, o fallos > 5 % en los últimos 200.
- **Horario permitido:** configurable (inicial 08:00–20:00, America/La_Paz).

## Errores

| Código | Clase | Acción |
| --- | --- | --- |
| 4, 80007, 130429 | Transitorio | Reintento con backoff; bajar tasa |
| 131056 | Transitorio | Reintentar ese destinatario más tarde |
| 131000, 131016 | Transitorio | Reintentar; si persiste, revisar estado de la plataforma |
| 131049 | Destinatario | +24 h mínimo, un intento más, solo si sigue vigente |
| 131050 | Destinatario | No reintentar; `PlatformPreference` stop |
| 131026 | Destinatario | No reintentar en esta campaña; "no alcanzable" con fecha; excluir solo tras varias fallas seguidas |
| 132000–132016 | Plantilla | Pausar campaña; 132016 → plantilla nueva y nueva versión |
| 131042 | Facturación | Pausa global + alerta |
| 131048, 131031, 368 | Calidad/política | Pausa global + alerta |
| 190 | Configuración | Pausa global + alerta; rotar token |
| 100, 135000 | Configuración | No reintentar; corregir payload |

Fuente: https://developers.facebook.com/documentation/business-messaging/whatsapp/support/error-codes

## Bajas

- Capa plataforma: webhook `user_preferences` (stop/resume) y error 131050 → solo marketing.
- Capa empresa: botón "No recibir más", palabras clave (BAJA, STOP, SALIR…), pedidos fuera de WhatsApp registrados en la intranet.
- Alcance según la solicitud; una importación o un `resume` nunca borra una baja de la empresa; reactivar exige nuevo opt-in auditado.

## Webhooks suscritos

`messages`, `message_template_status_update`, `message_template_quality_update`, `template_category_update`, `user_preferences`, `account_alerts`, `account_update`, `phone_number_quality_update`, `business_capability_update`, `security`.
