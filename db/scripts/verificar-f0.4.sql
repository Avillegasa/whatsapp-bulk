-- Verificación de F0.4: tablas creadas e índice único filtrado en SendAttempt.Wamid.
-- Ejecutar (cmd, desde la raíz del repo):
--   docker exec -i wabulk-sqlserver sh -c "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -d WaBulk -W" < db\scripts\verificar-f0.4.sql
SET NOCOUNT ON;

PRINT '--- Tablas';
SELECT name AS Tabla
FROM sys.tables
WHERE name <> '__EFMigrationsHistory'
ORDER BY name;

PRINT '--- Índices únicos';
SELECT t.name AS Tabla, i.name AS Indice, i.has_filter AS TieneFiltro, i.filter_definition AS Filtro
FROM sys.indexes i
JOIN sys.tables t ON t.object_id = i.object_id
WHERE i.is_unique = 1 AND i.is_primary_key = 0
ORDER BY t.name, i.name;

PRINT '--- Migraciones aplicadas';
SELECT MigrationId, ProductVersion FROM __EFMigrationsHistory;
