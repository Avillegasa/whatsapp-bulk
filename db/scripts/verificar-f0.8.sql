-- Verificación F0.8: resultado de los escenarios de tools/webhook-replay (últimas 15 filas del inbox).
-- Ejecutar en cmd desde la raíz del repo (la clave se toma del contenedor, no se muestra):
--   docker exec -i wabulk-sqlserver sh -c "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -d WaBulk -W" < db\scripts\verificar-f0.8.sql
SET NOCOUNT ON;

PRINT '--- WebhookInbox (últimas 15)';
SELECT TOP 15 Id,
       Estado,
       Intentos,
       JSON_VALUE(Cuerpo, '$.entry[0].changes[0].field') AS Campo,
       COALESCE(JSON_VALUE(Cuerpo, '$.entry[0].changes[0].value.statuses[0].status'),
                JSON_VALUE(Cuerpo, '$.entry[0].changes[0].value.user_preferences[0].value'),
                JSON_VALUE(Cuerpo, '$.entry[0].changes[0].value.event')) AS Valor,
       (SELECT COUNT(*) FROM OPENJSON(Cuerpo, '$.entry')) AS Entries,
       LEFT(JSON_VALUE(Cuerpo, '$.entry[0].changes[0].value.statuses[0].biz_opaque_callback_data'), 13) AS AttemptIni,
       LEFT(Error, 80) AS Error
FROM WebhookInbox
ORDER BY Id DESC;

PRINT '--- MessageEvent del último intento con wamid';
SELECT e.Id, e.Estado, e.TimestampMeta, e.CreadoUtc
FROM MessageEvent e
WHERE e.AttemptId = (SELECT TOP 1 AttemptId FROM SendAttempt WHERE Wamid IS NOT NULL ORDER BY InicioUtc DESC)
ORDER BY e.Id;

PRINT '--- Último SendAttempt con wamid';
SELECT TOP 1 AttemptId, Estado, EstadoEntrega, LEFT(Wamid, 20) AS WamidIni
FROM SendAttempt
WHERE Wamid IS NOT NULL
ORDER BY InicioUtc DESC;
