-- Verificación F0.11: capacidad del portafolio (destinatarios únicos en la ventana móvil de 24 h).
-- Ejecutar en cmd desde la raíz del repo (la clave se toma del contenedor, no se muestra):
--   docker exec -i wabulk-sqlserver sh -c "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -C -I -d WaBulk -W" < db\scripts\verificar-f0.11.sql
SET NOCOUNT ON;

PRINT '--- Destinatarios únicos vigentes por portafolio (lo que cuenta contra el presupuesto)';
SELECT PortafolioId, COUNT(DISTINCT PhoneE164) AS DestinatariosVigentes, COUNT(*) AS Reservas
FROM CapacityReservation
WHERE Estado IN ('Reservada', 'Consumida') AND ExpiraUtc > SYSUTCDATETIME()
GROUP BY PortafolioId;

PRINT '--- Reservas por estado';
SELECT Estado, COUNT(*) AS Reservas
FROM CapacityReservation
GROUP BY Estado;

PRINT '--- Últimas 20 reservas';
SELECT TOP 20 Id, PortafolioId, PhoneE164, DispatchJobId, Estado, ReservadaUtc, ExpiraUtc
FROM CapacityReservation
ORDER BY Id DESC;

PRINT '--- Trabajos pospuestos por falta de capacidad (RetryScheduled sin intentos)';
SELECT Id, PhoneE164, ProximoIntentoUtc
FROM DispatchJob
WHERE EstadoDespacho = 'RetryScheduled' AND Intentos = 0;
