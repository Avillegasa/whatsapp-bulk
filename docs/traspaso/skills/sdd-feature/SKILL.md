---
name: sdd-feature
description: Inicia una feature del roadmap en SDD. Define la feature spec con Andrés usando AskUserQuestion, crea la rama spec/YYYY-MM-DD-<feature> y la carpeta specs/YYYY-MM-DD-<feature>/ con requirements.md, validation.md y plan.md. Úsala con "siguiente feature", "armemos la feature X", "sigamos con la fase N", "nueva spec" o "/sdd-feature".
argument-hint: "[paso del roadmap o nombre, ej. 1.3 o cola-con-lease]"
---

# Feature spec

Pedido: $ARGUMENTS (vacío = el siguiente paso Pendiente de `specs/roadmap.md`).

## 1. Ubicar

1. Lee `CLAUDE.md`, `specs/roadmap.md`, `specs/mission.md` y `specs/tech-stack.md`; también `data-model.md`, `rules.md`, `ui-design.md` y `decisions.md` si existen y aplican.
2. Identifica el paso del roadmap. Si sus dependencias no están en Hecho, detente y dilo en una línea; con `AskUserQuestion` ofrece tomar otro paso (Recommended) o seguir igual como excepción registrada en `requirements.md`.
3. Si hay otra feature abierta (rama `spec/*` sin merge), pregunta con `AskUserQuestion` si se cierra primero.

## 2. Definir con Andrés (AskUserQuestion obligatorio)

Antes de escribir, define la feature en rondas de `AskUserQuestion`: hasta 4 preguntas por ronda, opciones cerradas, la recomendada primero con "(Recommended)" y su consecuencia en la descripción. Cubre, en este orden:

1. Problema y objetivo: qué cambia para el usuario cuando esté lista.
2. Alcance: qué incluye y qué excluye.
3. Flujo principal, flujos alternativos y casos borde.
4. Restricciones técnicas, de negocio y de seguridad (las reglas de `specs/` y `CLAUDE.md` aplican siempre; no se vuelven a preguntar).
5. Validación: qué se prueba automáticamente, qué con Meta real y qué confirma Andrés.

Los hechos (código, documentación oficial) los buscas tú y los traes como contexto de la pregunta. Todo dato de Meta, Microsoft o Angular se confirma en la documentación oficial; la URL queda en `requirements.md`. Si el PoC ya resolvió algo parecido, cita su sección del traspaso o su archivo como referencia.

## 3. Crear

1. Nombre: `YYYY-MM-DD-<feature>`, con la fecha de hoy y la feature en kebab-case y en español (ej. `2026-12-02-cola-con-lease`).
2. Rama desde `main` actualizada: `git switch main`, `git pull --ff-only` (si hay remoto), `git switch -c spec/YYYY-MM-DD-<feature>`. Si `CLAUDE.md` define otra convención de ramas, se sigue esa.
3. Carpeta `specs/YYYY-MM-DD-<feature>/` con las tres plantillas de `plantillas/`:
   - `requirements.md`: título, problema, contexto de uso, objetivo, alcance, comportamiento esperado, restricciones, enfoque técnico, decisiones y notas.
   - `validation.md`: criterios de aceptación Dado/Cuando/Entonces numerados (CA-1…), cómo se verifica cada uno, invariantes, escenarios adversariales previstos y tablas de evidencia vacías.
   - `plan.md`: tareas numeradas, pequeñas y en orden, cada una con los CA que cubre y su comprobación.
4. Comprobaciones: cada CA es observable y medible; cada CA tiene al menos una tarea; ninguna tarea queda sin CA (salvo andamiaje, marcado como tal).

## 4. Aprobar

1. Muestra un resumen: objetivo, alcance, cantidad de CA y de tareas, decisiones abiertas.
2. `AskUserQuestion`: "¿Apruebas la feature spec?" → Aprobar (Recommended) / Ajustar (indica qué).
3. Con la aprobación: en `specs/roadmap.md` el paso pasa a "En curso" con el enlace a la carpeta; commit `spec(<feature>): feature spec`.
4. Siguiente: skill `sdd-implementar`.
