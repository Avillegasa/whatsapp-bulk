-- Verificación F1.8: pausas y alertas (quién reanudó), revisiones de envíos inciertos y AuditLog del monitor.
-- Ejecutar en cmd desde la raíz del repo (la clave se toma del contenedor, no se muestra):
--   docker exec -i wabulk-sqlserver sh -c "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -I -d WaBulk -W" < db\scripts\verificar-f1.8.sql
SET NOCOUNT ON;

PRINT '== Pausas del despacho (las 5 últimas): LevantadaPor = usuario de la intranet o windows:usuario';
SELECT TOP (5) p.Id, p.Alcance, p.Motivo, CONVERT(varchar(19), p.DesdeUtc, 120) AS DesdeUtc,
       CONVERT(varchar(19), p.LevantadaUtc, 120) AS LevantadaUtc, p.LevantadaPor
FROM DispatchPause p
ORDER BY p.Id DESC;

PRINT '== Alertas (las 5 últimas)';
SELECT TOP (5) a.Id, a.Tipo, a.Clave, a.Ocurrencias, a.Origen, CONVERT(varchar(19), a.ResueltaUtc, 120) AS ResueltaUtc,
       a.ResueltaPor, LEFT(a.Mensaje, 80) AS Mensaje
FROM Alert a
ORDER BY a.Id DESC;

PRINT '== Envíos inciertos pendientes (NeedsReview)';
SELECT j.Id, j.CampaignVersionId, '…' + RIGHT(j.PhoneE164, 4) AS Tel, CONVERT(varchar(19), a.InicioUtc, 120) AS InicioUtc
FROM DispatchJob j JOIN SendAttempt a ON a.DispatchJobId = j.Id
WHERE j.EstadoDespacho = 'NeedsReview' AND a.Estado = 'SubmissionUnknown';

PRINT '== AuditLog del monitor (reanudar y revisiones)';
SELECT TOP (10) CONVERT(varchar(19), a.FechaUtc, 120) AS FechaUtc, a.Usuario AS Quien, a.Accion, a.Entidad, a.EntidadId,
       LEFT(a.Despues, 120) AS Despues
FROM AuditLog a
WHERE a.Accion IN ('ReanudarDespacho', 'RevisarEnvio')
ORDER BY a.Id DESC;
