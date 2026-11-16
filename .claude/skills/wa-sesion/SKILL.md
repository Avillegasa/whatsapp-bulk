---
name: wa-sesion
description: Protocolo de inicio, trabajo y cierre de cada sesión de Claude Code en el proyecto WhatsApp Bulk. Úsala cuando Andrés diga "Sigamos con la tarea F0.x", "Sigamos con WhatsApp", "cierra la tarea" o "registra el avance".
argument-hint: "[ID de tarea, ej. F0.3]"
---

# Sesión de trabajo — WhatsApp Bulk

Tarea pedida: $ARGUMENTS (si viene vacío, usa la "Siguiente tarea" de `docs/PROGRESO.md`).

## 1. Al iniciar

1. Lee `CLAUDE.md`, `docs/PROGRESO.md` y la tarea en `docs/PLAN.md`.
2. Revisa dependencias: si alguna tarea de "Depende de" no está en **Verificado**, detente y dilo en una línea.
3. Si la tarea es de **Tú** (Andrés), no la implementes: dale los pasos numerados, cada uno etiquetado `🖥 Terminal` o `🖱 Manual — panel`, con lo que debe pegar de vuelta. Si son pasos de Meta, remite a `docs/META-SETUP.md`.
4. Si la tarea toca una API externa (Meta, Microsoft, Angular), consulta la documentación oficial antes de escribir código y anota la URL.
5. Resume en 5 líneas como máximo: objetivo, archivos que vas a crear o tocar, cómo se va a verificar. Espera el "ok" de Andrés solo si hay una decisión abierta; si no, empieza.
6. Marca la tarea **En curso** en `docs/PROGRESO.md`.

## 2. Durante

- Solo el alcance de la tarea. Lo que surja fuera de él va a "Pendientes y bloqueos" en `docs/PROGRESO.md`.
- Respeta las "Reglas que no se rompen" de `CLAUDE.md`.
- Commits pequeños con prefijo de la tarea (`F0.5: ...`). No hagas push sin que Andrés lo pida.
- Cuando tomes una decisión no obvia, explícala en 1–2 líneas con un ejemplo concreto del proyecto (Andrés está aprendiendo el dominio).
- Si necesitas una decisión de Andrés: máximo 3 opciones, la recomendada primero, una línea de consecuencia por opción.

## 3. Al cerrar

1. Ejecuta la skill `wa-verificar` para la tarea.
2. Actualiza `docs/PROGRESO.md`: estado y evidencia en la tabla, "Siguiente tarea", "Última actualización", entrada nueva arriba en la bitácora y pendientes.
3. Decisión nueva → fila nueva en `docs/DECISIONES.md` (siguiente D-0xx).
4. Si cambiaron comandos o estructura → actualiza `CLAUDE.md`.
5. Explica a Andrés en 2–4 líneas qué se hizo y por qué.
6. Imprime el bloque CIERRE para que lo pegue en Claude desktop.

### Plantilla de bitácora

```
### AAAA-MM-DD — F0.x <título> (Claude Code)
- Hecho: <qué se construyó>
- Verificación: <comando> → <resultado resumido>; <prueba> → <resultado>
- Archivos: <principales>
- Decisiones: <D-0xx o "ninguna">
- Pendientes: <lo que quedó fuera>
- Siguiente: <F0.y>
```

### Bloque CIERRE

```
CIERRE F0.x | estado: <Verificado/Hecho (sin verificar)/Bloqueado> | evidencia: <1 línea> | decisiones: <D-0xx o ninguna> | pendientes: <1 línea> | siguiente: F0.y | dudas: <o ninguna>
```
