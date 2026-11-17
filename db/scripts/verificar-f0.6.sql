-- Verificación F0.6: webhooks guardados en WebhookInbox (sin mostrar el cuerpo completo).
-- Ejecutar en cmd desde la raíz del repo (la clave se toma del contenedor, no se muestra):
--   docker exec -i wabulk-sqlserver sh -c "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -d WaBulk -W" < db\scripts\verificar-f0.6.sql
SET NOCOUNT ON;

SELECT Id,
       RecibidoUtc,
       Estado,
       DATALENGTH(Cuerpo) / 2 AS Caracteres,
       JSON_VALUE(Cuerpo, '$.entry[0].id') AS EntryId,
       JSON_VALUE(Cuerpo, '$.entry[0].changes[0].field') AS Campo,
       JSON_VALUE(Cuerpo, '$.entry[0].changes[0].value.statuses[0].status') AS EstadoEntrega,
       JSON_VALUE(Cuerpo, '$.entry[0].changes[0].value.statuses[0].biz_opaque_callback_data') AS AttemptId
FROM WebhookInbox
ORDER BY Id;
