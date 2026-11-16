# WhatsApp Bulk — Envíos masivos por WhatsApp Cloud API

Sistema interno de la empresa para enviar promociones por WhatsApp a clientes con consentimiento, usando la API oficial de Meta (Cloud API). Sin BSP, sin WhatsApp Web, sin extensiones.

## Antes de cualquier tarea (obligatorio)

1. Lee `docs/PROGRESO.md` (estado, siguiente tarea, última sesión) y la tarea en `docs/PLAN.md`.
2. Sigue la skill `wa-sesion` (inicio, trabajo y cierre de sesión).
3. Trabaja solo en la tarea indicada. Lo que surja fuera de alcance va a "Pendientes" en `docs/PROGRESO.md`.
4. Una tarea no está **Verificado** sin evidencia (salida de comando, prueba o confirmación de Andrés). Usa la skill `wa-verificar`.

## Fuentes de verdad

| Archivo | Contenido |
| --- | --- |
| `docs/ARQUITECTURA.md` | Componentes, flujo, modelo de datos, estados, errores, reglas de bajas y capacidad |
| `docs/PLAN.md` | Tareas por fase con quién las hace y cómo se verifican |
| `docs/PROGRESO.md` | Estado de cada tarea y bitácora |
| `docs/DECISIONES.md` | Decisiones tomadas (D-001…) y su motivo |
| `docs/META-SETUP.md` | Configuración en Meta e IDs no secretos |

Documento de diseño completo (Claude Docs): https://claude.ai/artifact/4QbfsuRFZo1uKJcBwJaLgu

## Stack

- Frontend: Angular 21 (standalone, signals) en `src/frontend/`.
- Backend: .NET 10 (ASP.NET Core, Worker Service) en `src/backend/`.
- Base de datos: SQL Server (2022 en Docker para desarrollo). También es la cola.
- Autenticación: usuarios propios (ASP.NET Core Identity, cookie) con roles Operador, Aprobador, Admin. Diseñar para agregar Microsoft Entra ID más adelante sin rehacer la autorización.
- Despliegue: contenedores Docker (Api, Webhooks, Worker y frontend con nginx).
- Repositorio: GitHub (privado).

## Estructura

```
src/backend/    Solución WaBulk.sln (Domain, Infrastructure, Api, Webhooks, Worker, Tests)
src/frontend/   App Angular (desde la Fase 1)
db/scripts/     Scripts SQL de apoyo (consultas de verificación, datos de prueba)
deploy/docker/  Dockerfiles y compose (dev y prod)
tools/          Utilidades de desarrollo (replay de webhooks firmados)
docs/           Plan, progreso, decisiones, arquitectura, setup de Meta
.claude/skills/ Skills de este proyecto (wa-sesion, wa-verificar)
```

## Reglas que no se rompen

1. **Verificar antes de asumir.** Endpoints, payloads, códigos de error y sintaxis de Meta, Microsoft o Angular se confirman en la documentación oficial antes de escribir código. Anota la URL en un comentario o en la bitácora cuando la decisión no sea obvia.
2. **Versión de Graph API configurable** (`Meta:GraphApiVersion`), nunca literal en el código.
3. **Anti-duplicados:** se inserta y confirma `SendAttempt` antes del POST; el POST lleva `biz_opaque_callback_data` = AttemptId; sin política de reintento automático genérica (Polly/Resilience) sobre `/messages`.
4. **Sin respuesta confiable → `SubmissionUnknown`**, nunca reenvío automático.
5. **Cola:** reclamo en transacción corta con lease (`LockedBy`, `LeaseUntilUtc`); nunca mantener bloqueos durante una llamada HTTP; cada actualización verifica que el worker sigue siendo dueño del lease.
6. **Webhooks:** validar `X-Hub-Signature-256` sobre los bytes crudos con comparación de tiempo constante; guardar en `WebhookInbox` y recién responder 200; procesar fuera de la petición; idempotente.
7. **Estados:** despacho y entrega son estados separados; el de entrega solo avanza.
8. **Bajas:** consentimiento de la empresa y preferencias de plataforma son capas separadas; se revisan al encolar y al enviar; un `resume` de plataforma nunca borra una baja de la empresa.
9. **Reintentos** solo para códigos transitorios (4, 80007, 130429, 131056, 131000, 131016), con backoff y jitter, máximo 5, nunca después de `ExpiraUtc`.
10. **Secretos** (token, App Secret, verify token, PIN) nunca en el repositorio: `dotnet user-secrets` en desarrollo, variables de entorno o `.env` (ignorado por git) en Docker.
11. **Índices únicos** sobre columnas que admiten NULL siempre filtrados (`WHERE col IS NOT NULL`).
12. **Alcance:** solo promociones (marketing) en la primera etapa. Mensajes de cobranza o saldos vencidos: nunca.

## Comandos

Mantener esta sección al día. Todos funcionan en cmd desde la raíz del repo.

- SDK: `dotnet --version` (10.x)
- Backend: `dotnet build src/backend/WaBulk.sln` · `dotnet test src/backend/WaBulk.sln`
- Ejecutar Api: `dotnet run --project src/backend/WaBulk.Api` → `GET /health` responde `Healthy`
- Ejecutar Webhooks: `dotnet run --project src/backend/WaBulk.Webhooks` → `GET /health`
- Ejecutar Worker: `dotnet run --project src/backend/WaBulk.Worker`
- SQL Server local (desde F0.4): `docker compose -f deploy/docker/compose.dev.yml up -d sqlserver`
- Frontend (Fase 1): `cd src/frontend && npm ci && npx ng build` · `npx ng test`

Notas de la solución:
- `src/backend/Directory.Build.props` fija `net10.0`, nullable y warnings como errores para todos los proyectos.
- Paquetes `Microsoft.AspNetCore.*` / `Microsoft.Extensions.*` en la línea 10.0.x (las 11.x apuntan a net11).
- Las pruebas de integración usan `WebApplicationFactory<ApiAssemblyMarker>` / `<WebhooksAssemblyMarker>` (no `Program`, que se repite en Api y Webhooks).

## Estilo

- Identificadores de código en inglés; tablas y columnas con los nombres de `docs/ARQUITECTURA.md`.
- Documentación, comentarios y mensajes de commit en español.
- Commits pequeños con prefijo de tarea: `F0.5: cliente Meta y SendAttempt`.
- Andrés usa **cmd** (Símbolo del sistema), no PowerShell: todo comando para él debe funcionar en cmd (continuación de línea con `^`, variables con `%VAR%`).
- Andrés está aprendiendo el dominio: al cerrar cada tarea explica en 2–4 líneas qué se hizo y por qué, con un ejemplo si ayuda.
