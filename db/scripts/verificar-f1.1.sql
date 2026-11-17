-- Verificación F1.1: mensajes entrantes (respuestas de clientes) guardados en WebhookInbox.
-- Muestra solo los últimos 4 dígitos del remitente.
-- Ejecutar en cmd desde la raíz del repo (la clave se toma del contenedor, no se muestra):
--   docker exec -i wabulk-sqlserver sh -c "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -d WaBulk -W" < db\scripts\verificar-f1.1.sql
SET NOCOUNT ON;

SELECT Id,
       RecibidoUtc,
       Estado,
       JSON_VALUE(Cuerpo, '$.entry[0].changes[0].value.messages[0].type') AS Tipo,
       JSON_VALUE(Cuerpo, '$.entry[0].changes[0].value.messages[0].text.body') AS Texto,
       RIGHT(JSON_VALUE(Cuerpo, '$.entry[0].changes[0].value.messages[0].from'), 4) AS Ultimos4,
       JSON_VALUE(Cuerpo, '$.entry[0].changes[0].value.messages[0].timestamp') AS TimestampMeta
FROM WebhookInbox
WHERE JSON_VALUE(Cuerpo, '$.entry[0].changes[0].field') = 'messages'
  AND JSON_QUERY(Cuerpo, '$.entry[0].changes[0].value.messages') IS NOT NULL
ORDER BY Id;
