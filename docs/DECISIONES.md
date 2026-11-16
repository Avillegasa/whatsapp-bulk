# Decisiones

Formato: ID · fecha · decisión · motivo · consecuencias. Las nuevas van al final. Una decisión se reemplaza con otra nueva que la cite, nunca se borra.

| ID | Fecha | Decisión | Motivo | Consecuencias |
| --- | --- | --- | --- | --- |
| D-001 | 2026-11-16 | Cloud API de Meta directa; sin BSP, sin WhatsApp Web ni extensiones | Integración oficial y controlada por la empresa | Configuración propia en Meta; toda la lógica de cola, estados y bajas es nuestra |
| D-002 | 2026-11-16 | Angular 21 | Mismo stack que otros sistemas internos | Angular 21 está en LTS hasta junio de 2027 (Angular 22 es la versión activa); planificar la subida a 22 antes de esa fecha |
| D-003 | 2026-11-16 | .NET 10 | LTS con soporte hasta noviembre de 2028 | — |
| D-004 | 2026-11-16 | SQL Server como base de datos y cola, sin broker | Volumen bajo (techo de 80 mensajes/s) y transacciones junto con los datos | Medir con carga real en Fase 1; un broker no resolvería por sí solo los duplicados |
| D-005 | 2026-11-16 | Usuarios propios con ASP.NET Core Identity (cookie) y roles Operador, Aprobador, Admin | Decisión de Andrés; Entra ID más adelante | Autorización por roles/políticas, independiente del proveedor de login, para sumar Entra ID sin rehacerla |
| D-006 | 2026-11-16 | Despliegue en contenedores Docker (Api, Webhooks, Worker, frontend con nginx) | Decisión de Andrés | Dónde corre SQL Server en producción: pendiente |
| D-007 | 2026-11-16 | Repositorio privado en GitHub | Decisión de Andrés | Nunca subir secretos (`.gitignore`, revisión en `wa-verificar`) |
| D-008 | 2026-11-16 | Remitente del PoC: número de prueba de Meta; producción: número dedicado de la empresa | Registrar un número en la API lo saca de la app de WhatsApp | Hasta 5 destinatarios en el PoC |
| D-009 | 2026-11-16 | Alcance: promociones primero; avisos de pedido y factura emitida mucho después; cobranza nunca | Decisión de Andrés; la política de WhatsApp prohíbe "debt collection" | Plantillas solo de marketing en la primera etapa |
| D-010 | 2026-11-16 | Anti-duplicados: `SendAttempt` antes del POST, `biz_opaque_callback_data` = AttemptId, `SubmissionUnknown` sin reenvío automático | No hay clave de idempotencia documentada en Cloud API | Revisión manual de intentos inciertos |
| D-011 | 2026-11-16 | Cupo de 200 destinatarios por 24 h reservado para envíos manuales | Hoy se envía a mano a 150–200 personas cada 2–4 semanas | Configurable; idealmente esos envíos migran al sistema |
| D-012 | 2026-11-16 | Regla de fatiga: 3 promociones sin interacción → pausa 60 días | Reducir bloqueos y reportes | Botón "Me interesa" en promociones como señal confiable |
| D-013 | 2026-11-16 | EF Core 10 para datos y migraciones; SQL crudo para reclamar la cola | Productividad + control fino del bloqueo | Propuesta; confirmar en F0.4 |
| D-014 | 2026-11-16 | Túnel de desarrollo: Microsoft Dev Tunnels persistente con acceso anónimo | Meta exige HTTPS público para webhooks | ID no adivinable; apagar cuando no se usa; la firma protege el receptor |
| D-015 | 2026-11-16 | PoC (Fase 0) en un portafolio de Meta propio de prueba, no en el de la empresa | La empresa aún no tiene portafolio de Meta | App, WABA, número de prueba y System User del PoC son descartables; en F1.1 se crean de nuevo en el portafolio real de la empresa (con verificación del negocio); repetir B.4–B.6 con la WABA real. Requisito previo de F1.1: portafolio de la empresa creado |
| D-016 | 2026-11-16 | Carpeta del proyecto: el clon `C:\Users\avillegas\dev\whatsapp-bulk` (repo privado `Avillegasa/whatsapp-bulk`); `Documents\Whatsapp-bulk-proy` queda obsoleta | Decisión de Andrés | Se copia todo al clon; cambiar la ruta en las instrucciones del proyecto de claude.ai, en `guia-del-proyecto.md` y en la skill `whatsapp-bulk-guia`; reconectar la carpeta en Claude desktop. Ramas: `main` y `dev` |
