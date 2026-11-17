-- Verificación F1.5: plantillas, webhooks de plantillas, toques de botones de plantilla y bajas por botón.
-- Ejecutar en cmd desde la raíz del repo (la clave se toma del contenedor, no se muestra):
--   docker exec -i wabulk-sqlserver sh -c "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -I -d WaBulk -W" < db\scripts\verificar-f1.5.sql
-- Los teléfonos se muestran solo con los últimos 4 dígitos.
SET NOCOUNT ON;

PRINT '== Plantillas (tabla Template)';
SELECT t.Id, t.MetaTemplateId, t.Nombre, t.Idioma, t.Categoria, t.Estado,
       CONVERT(varchar(19), t.EstadoUtc, 120) AS EstadoUtc, t.UltimoEvento, t.Calidad, t.CategoriaAnterior, t.CategoriaNueva,
       t.FormatoParametros, t.CreadaPor, CONVERT(varchar(19), t.SincronizadaUtc, 120) AS SincronizadaUtc,
       LEFT(t.MotivoEstado, 120) AS Motivo
FROM Template t
ORDER BY t.Nombre, t.Idioma;

PRINT '== Webhooks de plantillas (últimos 10)';
SELECT TOP (10) w.Id, CONVERT(varchar(19), w.RecibidoUtc, 120) AS RecibidoUtc, w.Estado,
       JSON_VALUE(w.Cuerpo, '$.entry[0].changes[0].field') AS Campo,
       JSON_VALUE(w.Cuerpo, '$.entry[0].changes[0].value.message_template_name') AS Plantilla,
       COALESCE(JSON_VALUE(w.Cuerpo, '$.entry[0].changes[0].value.event'),
                JSON_VALUE(w.Cuerpo, '$.entry[0].changes[0].value.new_quality_score'),
                JSON_VALUE(w.Cuerpo, '$.entry[0].changes[0].value.new_category')) AS Evento,
       LEFT(w.Error, 80) AS Error
FROM WebhookInbox w
WHERE JSON_VALUE(w.Cuerpo, '$.entry[0].changes[0].field') IN
      ('message_template_status_update', 'message_template_quality_update', 'template_category_update')
ORDER BY w.Id DESC;

PRINT '== Toques de botones de plantilla (type button): payload y texto reales que manda Meta';
SELECT TOP (10) m.Id, m.ContactId, CONVERT(varchar(19), m.FechaUtc, 120) AS FechaUtc, m.Tipo,
       m.BotonId AS Payload, m.Texto, LEFT(m.WamidContexto, 24) AS RespondeA
FROM InboundMessage m
WHERE m.Tipo = 'button'
ORDER BY m.Id DESC;

PRINT '== Bajas por botón (ConsentRecord OptOut, Fuente Boton)';
SELECT TOP (10) r.Id, r.ContactId, '…' + RIGHT(r.PhoneE164, 4) AS Tel, r.Estado, r.Fuente,
       CONVERT(varchar(19), r.FechaUtc, 120) AS FechaUtc, r.Evidencia
FROM ConsentRecord r
WHERE r.Fuente = 'Boton' AND r.Estado = 'OptOut'
ORDER BY r.Id DESC;

PRINT '== Últimos envíos de prueba (SendAttempt) y su estado de entrega';
SELECT TOP (5) a.AttemptId, CONVERT(varchar(19), a.InicioUtc, 120) AS InicioUtc, a.Estado, a.EstadoEntrega, a.CodigoError,
       '…' + RIGHT(j.PhoneE164, 4) AS Tel
FROM SendAttempt a JOIN DispatchJob j ON j.Id = a.DispatchJobId
ORDER BY a.InicioUtc DESC;

PRINT '== AuditLog de plantillas';
SELECT TOP (10) CONVERT(varchar(19), a.FechaUtc, 120) AS FechaUtc, a.Usuario AS Quien, a.Accion, a.EntidadId,
       LEFT(a.Despues, 150) AS Despues
FROM AuditLog a
WHERE a.Accion IN ('CrearPlantilla', 'SincronizarPlantillas')
ORDER BY a.Id DESC;
