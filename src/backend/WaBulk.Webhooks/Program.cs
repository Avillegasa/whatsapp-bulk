var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();

var app = builder.Build();

// Liveness: responde "Healthy" si el proceso está arriba. Las comprobaciones de SQL Server se agregan en F0.4.
app.MapHealthChecks("/health");

app.Run();
