-- Verificación F0.9: estado de la cola (DispatchJob), leases e índices de reclamo.
-- Ejecutar en cmd desde la raíz del repo (la clave se toma del contenedor, no se muestra). -I activa
-- QUOTED_IDENTIFIER, obligatorio para escribir en DispatchJob (tiene índices filtrados):
--   docker exec -i wabulk-sqlserver sh -c "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -I -d WaBulk -W" < db\scripts\verificar-f0.9.sql
SET NOCOUNT ON;

PRINT '--- DispatchJob por estado';
SELECT EstadoDespacho, COUNT(*) AS Trabajos
FROM DispatchJob
GROUP BY EstadoDespacho;

PRINT '--- Trabajos reclamados (lease)';
SELECT Id, LockedBy, LeaseUntilUtc, Intentos,
       CASE WHEN LeaseUntilUtc < SYSUTCDATETIME() THEN 'vencido' ELSE 'vigente' END AS Lease
FROM DispatchJob
WHERE EstadoDespacho = 'Claimed';

PRINT '--- Trabajos con su último intento';
SELECT j.Id, j.EstadoDespacho, j.Intentos, j.ProximoIntentoUtc, j.ExpiraUtc,
       a.Estado AS UltimoIntento, a.CodigoError, a.EstadoEntrega
FROM DispatchJob j
OUTER APPLY (SELECT TOP 1 * FROM SendAttempt s WHERE s.DispatchJobId = j.Id ORDER BY s.InicioUtc DESC) a
ORDER BY j.Id;

PRINT '--- Índices de la cola';
SELECT i.name, i.filter_definition
FROM sys.indexes i
WHERE i.object_id = OBJECT_ID('DispatchJob') AND i.name LIKE 'IX_DispatchJob_%';
