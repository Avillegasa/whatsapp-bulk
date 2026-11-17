-- Verificación F0.7: estado del inbox, eventos por wamid y estado de entrega de cada intento.
-- Ejecutar en cmd desde la raíz del repo (la clave se toma del contenedor, no se muestra):
--   docker exec -i wabulk-sqlserver sh -c "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -d WaBulk -W" < db\scripts\verificar-f0.7.sql
SET NOCOUNT ON;

PRINT '--- WebhookInbox';
SELECT Id, Estado, Intentos, ProximoIntentoUtc, LEFT(Error, 120) AS Error
FROM WebhookInbox
ORDER BY Id;

PRINT '--- MessageEvent (orden de llegada)';
SELECT Id, LEFT(Wamid, 20) AS WamidIni, AttemptId, Estado, TimestampMeta, CreadoUtc
FROM MessageEvent
ORDER BY Id;

PRINT '--- SendAttempt';
SELECT AttemptId, Estado, EstadoEntrega, LEFT(Wamid, 20) AS WamidIni
FROM SendAttempt
ORDER BY InicioUtc;
