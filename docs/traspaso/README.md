# Paquete de traspaso al proyecto in-house

El PoC WhatsApp Bulk quedó en pausa el 2026-11-19 (D-040). Esta carpeta es lo que se lleva al repo nuevo.

| Archivo | Para qué | Dónde va en el repo nuevo |
| --- | --- | --- |
| `TRASPASO-INHOUSE.md` | Insumo de la planificación: estado, decisiones, modelo de datos, reglas, Meta, alcance ampliado, recomendaciones, banco de preguntas y roadmap sugerido | `docs/traspaso/TRASPASO-INHOUSE.md` |
| `CLAUDE-inicial.md` | Borrador del `CLAUDE.md` con el flujo SDD y las reglas no negociables | `CLAUDE.md` (raíz) |
| `skills/sdd-planificar/` | Spec principal (`specs/mission.md`, `tech-stack.md`, `roadmap.md`) con `/grill-me` | `.claude/skills/sdd-planificar/` |
| `skills/sdd-feature/` | Feature spec (`requirements.md`, `validation.md`, `plan.md`) y rama | `.claude/skills/sdd-feature/` |
| `skills/sdd-implementar/` | Tareas de `plan.md` con `AskUserQuestion` | `.claude/skills/sdd-implementar/` |
| `skills/sdd-adversarial/` | Testing adversarial con Codex | `.claude/skills/sdd-adversarial/` |
| `skills/sdd-cerrar/` | Puerta de validación, roadmap y merge a `main` | `.claude/skills/sdd-cerrar/` |

## Cómo empezar en el repo nuevo (cmd, desde la raíz del repo nuevo)

```cmd
xcopy /E /I "C:\Users\avillegas\dev\whatsapp-bulk\docs\traspaso" docs\traspaso
xcopy /E /I docs\traspaso\skills .claude\skills
copy docs\traspaso\CLAUDE-inicial.md CLAUDE.md
```

Luego, en Claude Code: "planifiquemos" (skill `sdd-planificar`).

Requisitos en la máquina: skill de usuario `grilling` (la llama `/grill-me`) y el plugin de Codex (`/codex:setup` para comprobarlo).
