---
name: wa-verificar
description: Verifica con evidencia que una tarea del proyecto WhatsApp Bulk cumple los criterios de docs/PLAN.md antes de marcarla Verificado, o audita el avance registrado. Úsala al cerrar una tarea, con "verifica F0.x" o con "verifica el avance".
argument-hint: "[ID de tarea | avance]"
---

# Verificación — WhatsApp Bulk

Objetivo: $ARGUMENTS

## Verificar una tarea

1. Lee los criterios de **Verificación** de la tarea en `docs/PLAN.md`.
2. Ejecuta cada comando o prueba. Conserva la evidencia resumida (líneas clave de la salida, número de pruebas pasadas).
3. Corre los chequeos generales de abajo que apliquen a lo que tocó la tarea.
4. Criterios que dependen de Andrés (mensaje recibido en su teléfono, paso en el panel de Meta): pídeselos y espera su confirmación. Nunca los des por cumplidos.
5. Resultado:
   - **Verificado:** todos los criterios y chequeos pasan.
   - **Hecho (sin verificar):** el código está, pero falta evidencia o confirmación de Andrés.
   - **Bloqueado:** algo falla o falta una dependencia; di qué y quién lo resuelve.
6. Escribe el resultado y la evidencia en la tabla de `docs/PROGRESO.md`.

## Auditar el avance ("verifica el avance")

1. Para cada tarea en **Verificado** en `docs/PROGRESO.md`, confirma que la bitácora tiene evidencia concreta.
2. Corre hoy el build y las pruebas completas; si algo que estaba verificado falla, márcalo **Bloqueado** con la causa.
3. Reporta en una tabla corta: tarea · registrado · estado real · acción.

## Chequeos generales

- Sin secretos en el repositorio:
  `git grep -nE "EAA[A-Za-z0-9]{20,}"` debe devolver vacío (tokens de Meta); revisa que `.env` y `appsettings.*.local.json` estén ignorados (`git check-ignore`).
- Versión de Graph API no literal en el código:
  `git grep -nE "graph\.facebook\.com/v[0-9]+" -- src` solo puede aparecer en configuración, nunca en `.cs` o `.ts`.
- Sin reintento automático sobre el envío: el cliente de `/messages` no usa `AddStandardResilienceHandler`, `AddResilienceHandler` ni políticas de retry de Polly.
- Índices únicos sobre columnas que admiten NULL son filtrados (`WHERE ... IS NOT NULL`) en la migración.
- Build y pruebas:
  `dotnet build src/backend/WaBulk.sln` sin warnings · `dotnet test src/backend/WaBulk.sln`
  Frontend (desde Fase 1): `npx ng build` · `npx ng test`
