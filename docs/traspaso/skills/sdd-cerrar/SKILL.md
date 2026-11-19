---
name: sdd-cerrar
description: Cierra una feature SDD. Comprueba que validation.md tenga evidencia completa y el testing adversarial resuelto, actualiza specs/roadmap.md y hace el merge de la rama spec/... a main. Úsala con "cierra la feature", "mergea", "terminamos la spec" o "/sdd-cerrar".
argument-hint: "[carpeta de la feature]"
---

# Cierre y merge

## 1. Puerta (todo debe cumplirse, comprobado ahora y no de memoria)

- [ ] `plan.md`: todas las tareas, incluidas las `H-n`, marcadas con evidencia.
- [ ] `validation.md`: cada CA en OK con evidencia; ninguno "Pendiente de Andrés"; cada hallazgo adversarial con disposición.
- [ ] Build sin advertencias y todas las pruebas en verde.
- [ ] Sin secretos en el repo: `git grep -nE "EAA[A-Za-z0-9]{20,}"` vacío; `.env` y archivos locales ignorados (`git check-ignore`).
- [ ] Reglas de `CLAUDE.md` que la feature tocó, comprobadas (por ejemplo: versión de Graph API no literal, sin reintento automático sobre `/messages`, índices únicos con NULL filtrados).

Si algo falla, detente, dilo en una línea y vuelve a `sdd-implementar` o `sdd-adversarial`.

## 2. Actualizar la documentación

1. `specs/roadmap.md`: el paso pasa a "Hecho" con la fecha y el enlace a la carpeta. Los pendientes de la feature se proponen como pasos nuevos con `AskUserQuestion` (agregar al roadmap / al backlog / descartar).
2. `specs/decisions.md`: decisiones de arquitectura nuevas que salieron de la feature.
3. `CLAUDE.md`: comandos, estructura o convenciones nuevas.
4. `requirements.md`: estado "Cerrada".
5. Commit `spec(<feature>): cierre`.

## 3. Merge a main

1. `AskUserQuestion` sobre la forma del merge: PR en GitHub con `gh pr create` y merge (Recommended si el repo tiene remoto) / merge local con `git merge --no-ff` / todavía no.
2. Ejecuta lo elegido; el push y el merge se hacen solo con esa confirmación.
3. Después: `git switch main`, actualizar, y borrar la rama de la feature si Andrés lo aprueba.

## 4. Explicar

En 2–4 líneas: qué hace ahora el sistema que antes no hacía, por qué se diseñó así y un ejemplo concreto. Luego indica el siguiente paso del roadmap (lo toma `sdd-feature`).
