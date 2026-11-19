---
name: sdd-adversarial
description: Testing adversarial de una feature SDD con Codex (plugin openai/codex-plugin-cc): revisión adversarial, pruebas que intentan romper los criterios de validation.md, triage de hallazgos y evidencia antes del merge. Úsala con "testing adversarial", "que Codex lo pruebe", "valida la feature" o "/sdd-adversarial".
argument-hint: "[carpeta de la feature]"
---

# Testing adversarial con Codex

Quien implementó (Claude) no es quien intenta romperlo (Codex). El objetivo es encontrar fallas antes del merge, no confirmar que funciona.

## 0. Requisitos

- Todas las tareas de `plan.md` marcadas y las pruebas propias en verde (`sdd-implementar` terminado).
- Árbol de trabajo limpio (todo commiteado) para que Codex revise la rama contra `main`.
- Codex listo: si nunca se usó en esta máquina o falla, pide a Andrés `/codex:setup` (y `!codex login` si el resultado lo indica).

## 1. Revisión adversarial (la lanza Andrés)

Claude no puede invocar `/codex:adversarial-review` ni `/codex:review` (tienen `disable-model-invocation`). Dale a Andrés el comando listo para pegar, con el foco tomado de `validation.md`:

```
/codex:adversarial-review --base main --background <foco: CA críticos, invariantes de specs/, escenarios adversariales previstos>
```

- Opcional, revisión de código estándar: `/codex:review --base main --background`.
- Seguimiento: `/codex:status` y `/codex:result <id>`.
- Pide a Andrés que te avise cuando termine o que pegue el resultado.

## 2. Pruebas adversariales (las escribe Codex)

Invoca la skill `codex:rescue` (o pide a Andrés `/codex:rescue --background …`) con una tarea así, completando las rutas:

> Lee specs/<carpeta>/requirements.md y specs/<carpeta>/validation.md. Tu objetivo es ROMPER la implementación de la rama actual frente a esos criterios y a las reglas de specs/ y CLAUDE.md. Escribe pruebas nuevas SOLO en <proyecto de pruebas>/Adversarial/<feature>, sin tocar código de producción. Prioriza: duplicados, concurrencia, reintentos, orden de eventos, entradas inválidas o límite, permisos, secretos, fechas y zonas horarias. Ejecuta las pruebas y reporta cada falla con: CA o regla afectada, pasos, resultado esperado y resultado obtenido.

## 3. Triage (Claude)

Para cada hallazgo de los pasos 1 y 2:

- **Confirmado:** se agrega a `plan.md` como tarea `H-n` con su prueba de regresión, se corrige y se vuelve a correr todo.
- **Descartado:** se anota en `validation.md` con el motivo (no aplica, ya cubierto, falso positivo) y la evidencia.
- **Duda o decisión de producto:** `AskUserQuestion` a Andrés con opciones: corregir ahora (Recommended si toca un invariante) / pendiente en el roadmap / aceptar el riesgo.

Las pruebas adversariales que encontraron fallas quedan en el repo como regresión; las que no encontraron nada se conservan si cubren un invariante.

## 4. Repetir y registrar

1. Tras las correcciones, vuelve a correr las pruebas de Codex; si los cambios fueron grandes, pide a Andrés una segunda revisión adversarial.
2. Completa en `validation.md`: la tabla de evidencia (cada CA en OK, Falla o Pendiente de Andrés, con evidencia y fecha) y la tabla de testing adversarial con la disposición de cada hallazgo.
3. `AskUserQuestion`: "¿Damos la validación por cerrada?" → Sí (Recommended si no quedan CA pendientes) / Falta algo.
4. Siguiente: skill `sdd-cerrar`.
