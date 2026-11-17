-- Verificación F1.6: campañas, versiones, aprobaciones y trabajos por versión.
-- Ejecutar en cmd desde la raíz del repo (la clave se toma del contenedor, no se muestra):
--   docker exec -i wabulk-sqlserver sh -c "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -I -d WaBulk -W" < db\scripts\verificar-f1.6.sql
-- Los teléfonos se muestran solo con los últimos 4 dígitos.
SET NOCOUNT ON;

PRINT '== Campañas';
SELECT c.Id, c.Nombre, c.Estado, CONVERT(varchar(19), c.EstadoUtc, 120) AS EstadoUtc, c.VersionActualId, c.CreadaPor,
       LEFT(c.MotivoEstado, 60) AS Motivo, CONVERT(varchar(19), c.DespachoCompletoUtc, 120) AS DespachoCompletoUtc
FROM Campaign c
ORDER BY c.Id DESC;

PRINT '== Versiones (la vigente de cada campaña tiene Vigente = 1)';
SELECT v.CampaignId, v.Id, v.Numero, CASE WHEN c.VersionActualId = v.Id THEN 1 ELSE 0 END AS Vigente,
       v.NombrePlantilla, v.Idioma, v.Destinatarios, v.Excluidos, LEFT(v.Parametros, 80) AS Parametros,
       CONVERT(varchar(16), v.ProgramadaUtc, 120) AS ProgramadaUtc, CONVERT(varchar(16), v.ExpiraUtc, 120) AS ExpiraUtc,
       v.CreadaPor, v.AprobadaPor, v.RechazadaPor, LEFT(v.Hash, 12) AS Hash
FROM CampaignVersion v JOIN Campaign c ON c.Id = v.CampaignId
ORDER BY v.CampaignId DESC, v.Numero DESC;

PRINT '== Trabajos por versión y estado (OnHold = campaña en pausa)';
SELECT j.CampaignVersionId, j.EstadoDespacho, j.MotivoOmision, COUNT(*) AS Trabajos
FROM DispatchJob j
GROUP BY j.CampaignVersionId, j.EstadoDespacho, j.MotivoOmision
ORDER BY j.CampaignVersionId DESC, j.EstadoDespacho;

PRINT '== Últimos 10 trabajos de campañas con sus parámetros';
SELECT TOP (10) j.Id, j.CampaignVersionId, '…' + RIGHT(j.PhoneE164, 4) AS Tel, j.EstadoDespacho, j.MotivoOmision,
       LEFT(j.Parametros, 80) AS Parametros
FROM DispatchJob j
WHERE j.CampaignVersionId IS NOT NULL
ORDER BY j.Id DESC;

PRINT '== Puerta de la Fase 1: dos intentos Accepted para el mismo (versión, contacto) — debe estar vacío';
SELECT j.CampaignVersionId, j.ContactId, COUNT(*) AS Aceptados
FROM SendAttempt a JOIN DispatchJob j ON j.Id = a.DispatchJobId
WHERE a.Estado = 'Accepted' AND j.CampaignVersionId IS NOT NULL
GROUP BY j.CampaignVersionId, j.ContactId
HAVING COUNT(*) > 1;

PRINT '== AuditLog de campañas';
SELECT TOP (15) CONVERT(varchar(19), a.FechaUtc, 120) AS FechaUtc, a.Usuario AS Quien, a.Accion, a.EntidadId AS Campana,
       LEFT(a.Despues, 150) AS Despues
FROM AuditLog a
WHERE a.Entidad = 'Campana'
ORDER BY a.Id DESC;
