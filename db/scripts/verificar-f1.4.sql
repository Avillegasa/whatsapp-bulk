-- Verificación F1.4: contactos, mensajes entrantes, ofertas de promociones y consentimiento vigente.
-- Ejecutar en cmd desde la raíz del repo (la clave se toma del contenedor, no se muestra):
--   docker exec -i wabulk-sqlserver sh -c "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -I -d WaBulk -W" < db\scripts\verificar-f1.4.sql
-- Los teléfonos se muestran solo con los últimos 4 dígitos.
SET NOCOUNT ON;

PRINT '== Contactos y consentimiento vigente (último ConsentRecord por teléfono)';
SELECT c.Id, '…' + RIGHT(c.PhoneE164, 4) AS Tel, c.UserId, c.Nombre, c.CreadoPor,
       COALESCE((SELECT TOP (1) r.Estado FROM ConsentRecord r
                 WHERE r.PhoneE164 = c.PhoneE164 AND r.Finalidad = 'Promociones'
                 ORDER BY r.FechaUtc DESC, r.Id DESC), 'SinConsentimiento') AS Consentimiento
FROM Contact c
ORDER BY c.Id;

PRINT '== Últimos 10 mensajes entrantes';
SELECT TOP (10) m.Id, m.ContactId, CONVERT(varchar(19), m.FechaUtc, 120) AS FechaUtc, m.Tipo,
       LEFT(m.Texto, 40) AS Texto, m.BotonId, LEFT(m.WamidContexto, 20) AS Contexto
FROM InboundMessage m
ORDER BY m.Id DESC;

PRINT '== Ofertas de promociones';
SELECT o.Id, o.ContactId, CONVERT(varchar(19), o.FechaUtc, 120) AS FechaUtc, o.OfrecidoPor, o.VersionTexto, o.Estado,
       o.CodigoError, o.Respuesta, CONVERT(varchar(19), o.RespuestaUtc, 120) AS RespuestaUtc
FROM ConsentOffer o
ORDER BY o.Id DESC;

PRINT '== Historial de consentimiento (últimos 10)';
SELECT TOP (10) r.Id, r.ContactId, '…' + RIGHT(r.PhoneE164, 4) AS Tel, r.Estado, r.Fuente,
       CONVERT(varchar(19), r.FechaUtc, 120) AS FechaUtc, LEFT(r.WamidOrigen, 20) AS WamidOrigen, r.Evidencia
FROM ConsentRecord r
ORDER BY r.Id DESC;

PRINT '== AuditLog de F1.4 (últimas 10)';
SELECT TOP (10) CONVERT(varchar(19), a.FechaUtc, 120) AS FechaUtc, a.Usuario, a.Accion, a.Entidad, a.EntidadId, LEFT(a.Despues, 120) AS Despues
FROM AuditLog a
WHERE a.Accion IN ('OfrecerPromociones', 'BajaManual', 'ImportarContactos', 'CrearLista', 'AgregarALista', 'QuitarDeLista')
ORDER BY a.Id DESC;

PRINT '== WebhookInbox sin procesar o en revisión';
SELECT Id, Estado, Intentos, LEFT(Error, 120) AS Error FROM WebhookInbox WHERE Estado <> 'Procesado' ORDER BY Id;
