-- Verificación F0.10: bajas en sus dos capas y estado vigente por teléfono.
-- Ejecutar en cmd desde la raíz del repo (la clave se toma del contenedor, no se muestra):
--   docker exec -i wabulk-sqlserver sh -c "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -I -d WaBulk -W" < db\scripts\verificar-f0.10.sql
SET NOCOUNT ON;

PRINT '--- ConsentRecord (capa empresa, historial)';
SELECT Id, PhoneE164, Finalidad, Estado, Fuente, LEFT(Evidencia, 50) AS Evidencia, LEFT(WamidOrigen, 24) AS WamidOrigen,
       FechaUtc, RegistradoPor
FROM ConsentRecord
ORDER BY Id;

PRINT '--- PlatformPreference (capa plataforma, historial)';
SELECT Id, PhoneE164, Categoria, Valor, Origen, FechaUtc
FROM PlatformPreference
ORDER BY Id;

PRINT '--- Estado vigente por teléfono (el último registro de cada capa)';
WITH phones AS (
    SELECT PhoneE164 FROM ConsentRecord
    UNION SELECT PhoneE164 FROM PlatformPreference
)
SELECT p.PhoneE164,
       (SELECT TOP 1 Estado FROM ConsentRecord c
        WHERE c.PhoneE164 = p.PhoneE164 AND c.Finalidad = 'Promociones' ORDER BY c.FechaUtc DESC, c.Id DESC) AS Empresa,
       (SELECT TOP 1 Valor FROM PlatformPreference x
        WHERE x.PhoneE164 = p.PhoneE164 AND x.Categoria = 'marketing_messages' ORDER BY x.FechaUtc DESC, x.Id DESC) AS Plataforma
FROM phones p;

PRINT '--- DispatchJob omitidos y su motivo';
SELECT Id, PhoneE164, EstadoDespacho, MotivoOmision
FROM DispatchJob
WHERE EstadoDespacho = 'Skipped';
