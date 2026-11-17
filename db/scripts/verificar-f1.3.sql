-- Verificación F1.3: usuarios (activo, roles, bloqueo) y AuditLog.
-- Ejecutar en cmd desde la raíz del repo (la clave se toma del contenedor, no se muestra):
--   docker exec -i wabulk-sqlserver sh -c "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -I -d WaBulk -W" < db\scripts\verificar-f1.3.sql
SET NOCOUNT ON;

PRINT '== Usuarios';
SELECT u.UserName AS Usuario,
       STRING_AGG(r.Name, ', ') WITHIN GROUP (ORDER BY r.Name) AS Roles,
       CASE u.Activo WHEN 1 THEN 'Activo' ELSE 'Desactivado' END AS Estado,
       CASE WHEN u.LockoutEnd > SYSDATETIMEOFFSET() THEN CONVERT(varchar(19), u.LockoutEnd, 120) ELSE '-' END AS BloqueadoHasta
FROM AspNetUsers u
LEFT JOIN AspNetUserRoles ur ON ur.UserId = u.Id
LEFT JOIN AspNetRoles r ON r.Id = ur.RoleId
GROUP BY u.UserName, u.Activo, u.LockoutEnd
ORDER BY u.UserName;

PRINT '== AuditLog (últimas 20; EntidadId del usuario se traduce a su nombre)';
SELECT TOP (20)
       CONVERT(varchar(19), a.FechaUtc, 120) AS FechaUtc,
       a.Usuario AS Quien,
       a.Accion,
       COALESCE(u.UserName, a.EntidadId) AS Sobre,
       a.Antes,
       a.Despues
FROM AuditLog a
LEFT JOIN AspNetUsers u ON a.Entidad = 'Usuario' AND u.Id = a.EntidadId
ORDER BY a.Id DESC;
