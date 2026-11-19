---
name: sdd-planificar
description: Fase de Planificación SDD. Crea o actualiza la spec principal en specs/ (mission.md, tech-stack.md, roadmap.md y, si hacen falta, data-model.md, rules.md, ui-design.md y decisions.md) entrevistando a Andrés con la skill grilling (/grill-me). Úsala con "planifiquemos", "armemos la spec principal", "actualiza la misión/el stack/el roadmap" o "/sdd-planificar".
argument-hint: "[mission | tech-stack | roadmap | todo]"
---

# Planificación — spec principal

Objetivo: $ARGUMENTS (vacío = todo, en orden mission → tech-stack → roadmap).

La spec principal es la base de todo lo que sigue: misión, stack, estructura de datos, diseño de interfaces y orden de implementación. Ninguna feature se especifica sin ella.

## 1. Antes de preguntar

1. Lee `CLAUDE.md`, lo que exista en `specs/` y el insumo del proyecto anterior: `docs/traspaso/TRASPASO-INHOUSE.md` (su tabla de la sección 0 dice qué sección alimenta cada archivo).
2. Resume en 5 líneas como máximo: qué archivos vas a crear o tocar, qué ya está decidido (no se vuelve a discutir) y qué falta decidir.
3. Los hechos los buscas tú (repo, documentación oficial); las decisiones son de Andrés. Todo dato de Meta, Microsoft o Angular se confirma en la documentación oficial y la URL queda escrita en el archivo. Lo marcado [VERIFICAR] en el traspaso se verifica antes de pasarlo a una spec.

## 2. Entrevista

1. Invoca la skill `grilling` (la misma que dispara `/grill-me`) con el archivo en curso como tema. Parte del banco de preguntas de la sección 13 del traspaso y trae la respuesta recomendada en cada pregunta.
2. Un archivo a la vez: `mission.md` → `tech-stack.md` → `roadmap.md`. No pases al siguiente hasta que la frontera de decisiones del actual esté vacía.
3. Cuando una decisión tenga opciones cerradas, usa `AskUserQuestion` (máximo 4 opciones, la recomendada primero con "(Recommended)" y su consecuencia en la descripción).
4. Lo que quede sin decidir va a "Preguntas abiertas" del archivo; nunca se supone en silencio.

## 3. Escribir

Usa las plantillas de `plantillas/` (junto a esta skill):

- `mission.md`: problema, objetivo, usuarios y roles, alcance por etapa (incluye / excluye / nunca), métricas y puertas, principios no negociables, no-objetivos, responsables.
- `tech-stack.md`: stack con versiones y motivo, arquitectura (diagrama), patrones obligatorios, modelo de datos base (o enlace a `data-model.md`), seguridad, configuración, entornos, pruebas, despliegue, observabilidad, convenciones.
- `roadmap.md`: fases en orden de implementación; cada una con objetivo, pasos atómicos numerados (cada paso = una feature spec futura, con sus dependencias) y puerta de salida medible; más la pista paralela de Meta, negocio e Infraestructura.
- Diseño de interfaces: con Claude Design (artefacto de diseño o sistema de diseño), a partir del inventario de pantallas y los principios del traspaso (sección 10). Enlaza el resultado y resume tokens, componentes y pantallas en `specs/ui-design.md`.
- Decisiones de arquitectura: `specs/decisions.md` (ADR-001…), con motivo y consecuencias, citando la D-xxx del PoC cuando sea su antecedente.
- Reglas no negociables: en `specs/rules.md` si son muchas para `mission.md`.

Escritura: español; frases cortas; tablas para comparar; números, versiones y fechas concretos; nada de relleno.

## 4. Cerrar

1. Muestra el índice de lo escrito y las preguntas abiertas.
2. `AskUserQuestion`: "¿La spec principal refleja lo acordado?" → Sí (Recommended) / Ajustar (indica qué).
3. Con el sí: commit `specs: <qué cambió>` (sin push salvo que Andrés lo pida). Si cambiaron reglas, comandos o convenciones, actualiza `CLAUDE.md`.
4. Explica a Andrés en 2–4 líneas qué quedó definido y cuál es el primer paso del roadmap (lo toma la skill `sdd-feature`).
