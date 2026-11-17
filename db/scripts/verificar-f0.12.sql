-- Verificación F0.12: alertas y pausa global del despacho.
-- Ejecutar en cmd desde la raíz del repo (la clave se toma del contenedor, no se muestra):
--   docker exec -i wabulk-sqlserver sh -c "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -I -d WaBulk -W" < db\scripts\verificar-f0.12.sql
-- Para ver y levantar la pausa sin SQL: dotnet run --project src\backend\WaBulk.Worker -- alertas
--                                       dotnet run --project src\backend\WaBulk.Worker -- reanudar   (registra el usuario de Windows)
SET NOCOUNT ON;

PRINT '--- Pausa global activa (vacío = despacho activo)';
SELECT Id, Alcance, Motivo, AlertId, DesdeUtc
FROM DispatchPause
WHERE LevantadaUtc IS NULL;

PRINT '--- Alertas (últimas 20)';
SELECT TOP 20 Id, Tipo, Clave, Ocurrencias, Origen, LEFT(Mensaje, 80) AS Mensaje, CreadaUtc, UltimaUtc, ResueltaUtc, ResueltaPor
FROM Alert
ORDER BY Id DESC;

PRINT '--- Historial de pausas';
SELECT Id, Alcance, Motivo, AlertId, DesdeUtc, LevantadaUtc, LevantadaPor
FROM DispatchPause
ORDER BY Id DESC;
