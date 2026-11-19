---
name: sdd-implementar
description: Implementa una feature spec SDD tarea por tarea según specs/YYYY-MM-DD-<feature>/plan.md, con pruebas, evidencia y commits pequeños, preguntando cada decisión con AskUserQuestion. Úsala con "implementa", "sigamos con la tarea N", "continúa la feature" o "/sdd-implementar".
argument-hint: "[número de tarea | carpeta de la feature]"
---

# Implementación

Pedido: $ARGUMENTS (vacío = la primera tarea sin marcar de la feature activa).

## 1. Al iniciar

1. Ubica la feature activa: rama actual `spec/<carpeta>` ↔ `specs/<carpeta>/`. Si no coinciden o la feature no está aprobada, detente: primero `sdd-feature`.
2. Lee `requirements.md`, `plan.md` y `validation.md` de la feature. Consulta `specs/mission.md`, `specs/tech-stack.md` y los demás archivos de `specs/` solo cuando la tarea lo necesite.
3. Toma la tarea pedida o la primera sin marcar. Resume en 3 líneas: qué vas a hacer, qué archivos tocas y cómo lo vas a comprobar.

## 2. Durante

- Solo el alcance de la feature. Lo que surja fuera va a "Pendientes fuera de alcance" de `requirements.md` o como paso propuesto para `specs/roadmap.md`; nunca se implementa de paso.
- Toda API externa (Meta, Microsoft, Angular) se confirma en la documentación oficial antes de escribir código; la URL va en un comentario o en "Decisiones" de `requirements.md`.
- **Decisiones:** cualquier ambigüedad o elección que la spec no cubra → `AskUserQuestion` (máximo 4 opciones, la recomendada primero con "(Recommended)", la consecuencia de cada una en una línea). Nunca supongas. Registra la respuesta en "Decisiones" de `requirements.md`; si es de arquitectura, también en `specs/decisions.md`.
- Escribe primero la prueba cuando el CA lo permita; cada tarea termina con su comprobación ejecutada (salida resumida, nunca de memoria).
- Respeta las reglas no negociables de `specs/` y de `CLAUDE.md`.
- Explica las decisiones no obvias en 1–2 líneas con un ejemplo concreto del proyecto: Andrés está aprendiendo el dominio.
- Nunca pidas ni muestres secretos en el chat; si aparecen, avisa que hay que rotarlos.

## 3. Al terminar cada tarea

1. Marca la tarea `[x]` en `plan.md` con una línea de evidencia (comando → resultado).
2. Commit pequeño: `<feature>: tarea N — <qué>` (sin push salvo que Andrés lo pida).
3. Si la tarea reveló un CA faltante o mal escrito, propón el cambio a `validation.md` con `AskUserQuestion` antes de tocarlo.

## 4. Al terminar todas las tareas

1. Corre build, todas las pruebas y los chequeos de `validation.md` que no dependen de Codex; anota la evidencia en la tabla.
2. Los criterios que dependen de Andrés (teléfono, panel de Meta, navegador): pídeselos y espera su confirmación; nunca los des por cumplidos.
3. Siguiente: skill `sdd-adversarial`.
