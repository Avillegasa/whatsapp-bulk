-- Verificación F1.2: usuarios, roles y bloqueo (ASP.NET Core Identity).
-- Ejecutar en cmd desde la raíz del repo (la clave se toma del contenedor, no se muestra):
--   docker exec -i wabulk-sqlserver sh -c "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -I -d WaBulk -W" < db\scripts\verificar-f1.2.sql
SET NOCOUNT ON;

PRINT '== Roles (datos de la migración Identity)';
SELECT Name AS Rol FROM AspNetRoles ORDER BY Name;

PRINT '== Usuarios: rol, intentos fallidos y bloqueo (LockoutEnd en el futuro = bloqueado)';
SELECT u.UserName AS Usuario,
       STRING_AGG(r.Name, ', ') AS Roles,
       u.AccessFailedCount AS IntentosFallidos,
       u.LockoutEnd AS BloqueadoHasta,
       CASE WHEN u.LockoutEnd > SYSDATETIMEOFFSET() THEN 'SI' ELSE 'no' END AS BloqueadoAhora
FROM AspNetUsers u
LEFT JOIN AspNetUserRoles ur ON ur.UserId = u.Id
LEFT JOIN AspNetRoles r ON r.Id = ur.RoleId
GROUP BY u.UserName, u.AccessFailedCount, u.LockoutEnd
ORDER BY u.UserName;

PRINT '== Índices únicos de Identity (deben ser filtrados, regla 11)';
SELECT OBJECT_NAME(object_id) AS Tabla, name AS Indice, filter_definition AS Filtro
FROM sys.indexes WHERE is_unique = 1 AND OBJECT_NAME(object_id) LIKE 'AspNet%' AND has_filter = 1;
