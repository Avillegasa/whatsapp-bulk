-- Verificación F1.7: controles del despachador (tope semanal, fatiga, horario, tasa, freno, capacidad).
-- Ejecutar en cmd desde la raíz del repo (la clave se toma del contenedor, no se muestra):
--   docker exec -i wabulk-sqlserver sh -c "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -I -d WaBulk -W" < db\scripts\verificar-f1.7.sql
-- Los teléfonos se muestran solo con los últimos 4 dígitos. Horas en UTC (La Paz = UTC−4).
SET NOCOUNT ON;

PRINT '== Trabajos omitidos por control (LimiteFrecuencia, Fatiga, FatigaExcluido…) por versión';
SELECT j.CampaignVersionId, j.MotivoOmision, COUNT(*) AS Trabajos
FROM DispatchJob j
WHERE j.EstadoDespacho = 'Skipped'
GROUP BY j.CampaignVersionId, j.MotivoOmision
ORDER BY j.CampaignVersionId DESC;

PRINT '== Últimos 20 intentos de campañas: hora (tasa y horario) y resultado';
SELECT TOP (20) a.DispatchJobId, '…' + RIGHT(j.PhoneE164, 4) AS Tel, j.CampaignVersionId,
       CONVERT(varchar(23), a.InicioUtc, 121) AS InicioUtc,
       CONVERT(varchar(16), DATEADD(HOUR, -4, a.InicioUtc), 120) AS HoraLaPaz,
       a.Estado, a.EstadoEntrega, a.CodigoError
FROM SendAttempt a JOIN DispatchJob j ON j.Id = a.DispatchJobId
WHERE j.CampaignVersionId IS NOT NULL
ORDER BY a.InicioUtc DESC;

PRINT '== Tasa medida por segundo (últimos intentos de campañas): máximo por segundo';
SELECT TOP (5) CONVERT(varchar(19), a.InicioUtc, 120) AS Segundo, COUNT(*) AS Envios
FROM SendAttempt a JOIN DispatchJob j ON j.Id = a.DispatchJobId
WHERE j.CampaignVersionId IS NOT NULL
GROUP BY CONVERT(varchar(19), a.InicioUtc, 120)
ORDER BY COUNT(*) DESC;

PRINT '== Promociones por contacto en los últimos 7 días (tope D-031)';
SELECT '…' + RIGHT(j.PhoneE164, 4) AS Tel, COUNT(DISTINCT j.Id) AS Promociones
FROM SendAttempt a JOIN DispatchJob j ON j.Id = a.DispatchJobId
WHERE j.CampaignVersionId IS NOT NULL AND a.Estado IN ('Accepted', 'SubmissionUnknown')
  AND a.InicioUtc >= DATEADD(DAY, -7, SYSUTCDATETIME())
GROUP BY j.PhoneE164
ORDER BY Promociones DESC;

PRINT '== Campañas en pausa por el sistema (plantilla) y alertas abiertas';
SELECT c.Id, c.Nombre, c.Estado, LEFT(c.MotivoEstado, 100) AS Motivo FROM Campaign c WHERE c.Estado = 'Paused';
SELECT a.Id, a.Tipo, a.Clave, a.Ocurrencias, a.Origen, LEFT(a.Mensaje, 100) AS Mensaje
FROM Alert a WHERE a.ResueltaUtc IS NULL;

PRINT '== Límite real de Meta y reservas de capacidad vigentes';
SELECT PortafolioId, Limite, ValorMeta, Origen, CONVERT(varchar(19), ActualizadoUtc, 120) AS ActualizadoUtc FROM MessagingLimit;
SELECT PortafolioId, COUNT(DISTINCT PhoneE164) AS DestinatariosVigentes
FROM CapacityReservation WHERE Estado IN ('Reservada', 'Consumida') AND ExpiraUtc > SYSUTCDATETIME()
GROUP BY PortafolioId;
