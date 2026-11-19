# <Nombre del proyecto> — WhatsApp Cloud API in-house

Sistema interno de la empresa para enviar mensajes por WhatsApp con la API oficial de Meta (Cloud API) a clientes con consentimiento. Alcance y etapas: `specs/mission.md`. Antecedente: el PoC WhatsApp Bulk (`docs/traspaso/TRASPASO-INHOUSE.md`).

> Borrador para el repo nuevo. Completar "Stack", "Estructura" y "Comandos" al cerrar la planificación y la Fase 0.

## Metodología: SDD (obligatoria)

1. **Planificación** — `specs/mission.md`, `specs/tech-stack.md`, `specs/roadmap.md` (y `data-model.md`, `rules.md`, `ui-design.md`, `decisions.md` si existen). Skill `sdd-planificar`, entrevista con `/grill-me`.
2. **Feature spec** — por cada paso del roadmap: rama `spec/YYYY-MM-DD-<feature>` y carpeta `specs/YYYY-MM-DD-<feature>/` con `requirements.md`, `validation.md` y `plan.md`. Skill `sdd-feature`.
3. **Implementación** — tareas de `plan.md` en orden, con evidencia. Skill `sdd-implementar`.
4. **Testing adversarial** — Codex intenta romper la feature (`/codex:adversarial-review`, `/codex:rescue`). Skill `sdd-adversarial`.
5. **Merge a `main`** — solo con `validation.md` completo. Skill `sdd-cerrar`.

Reglas del flujo:

- **`AskUserQuestion` para toda decisión**, en planificación y en implementación: máximo 4 opciones, la recomendada primero con "(Recommended)". Nunca suponer.
- Antes de cualquier tarea: leer `specs/roadmap.md` y la feature activa (la carpeta que corresponde a la rama actual).
- Trabajar solo en la feature activa; lo que surja fuera va a "Pendientes fuera de alcance" de su `requirements.md` o como paso propuesto del roadmap.
- Nada está terminado sin evidencia: salida de comando, prueba o confirmación de Andrés.

## Fuentes de verdad

| Archivo | Contenido |
| --- | --- |
| `specs/mission.md` | Problema, objetivo, roles, alcance, métricas, principios |
| `specs/tech-stack.md` | Stack, arquitectura, patrones, seguridad, entornos, convenciones |
| `specs/roadmap.md` | Fases, pasos atómicos, puertas y estado |
| `specs/decisions.md` | Decisiones de arquitectura (ADR-001…) |
| `specs/YYYY-MM-DD-<feature>/` | Requisitos, validación y plan de cada feature |
| `docs/traspaso/TRASPASO-INHOUSE.md` | Lo aprendido en el PoC (consulta) |

## Reglas que no se rompen

Copiadas del PoC (traspaso, sección 7.1); confirmar o ajustar en la planificación.

1. **Verificar antes de asumir.** Endpoints, payloads, códigos de error y sintaxis de Meta, Microsoft o Angular se confirman en la documentación oficial; la URL queda en un comentario o en la feature spec.
2. **Versión de Graph API configurable**, nunca literal en el código.
3. **Anti-duplicados:** el intento se inserta y confirma antes del POST; el POST lleva `biz_opaque_callback_data` = AttemptId; sin política de reintento automático genérica sobre `/messages`.
4. **Sin respuesta confiable → `SubmissionUnknown`**, nunca reenvío automático.
5. **Cola:** reclamo en transacción corta con lease; nunca bloqueos durante una llamada HTTP; cada actualización verifica que el worker sigue siendo dueño del lease.
6. **Webhooks:** firma `X-Hub-Signature-256` sobre los bytes crudos con comparación de tiempo constante; guardar en el inbox y recién responder 200; procesar fuera de la petición; idempotente.
7. **Estados:** despacho y entrega son estados separados; el de entrega solo avanza.
8. **Consentimiento y bajas:** consentimiento de la empresa (por finalidad) y preferencias de plataforma son capas separadas; se revisan al encolar y al enviar; un `resume` de plataforma nunca borra una baja de la empresa.
9. **Reintentos** solo para códigos transitorios (4, 80007, 130429, 131056, 131000, 131016), con backoff y jitter, máximo 5, nunca después del vencimiento.
10. **Secretos** (token, App Secret, verify token, PIN) nunca en el repositorio ni en el chat.
11. **Índices únicos** sobre columnas que admiten NULL, siempre filtrados.
12. **Cobranza o saldos vencidos:** nunca.
13. **Recursos visibles en la red** (túneles y similares): prefijo `avit-<proyecto>` (pedido de Infraestructura).

## Stack

<Completar desde `specs/tech-stack.md`.>

## Estructura

<Completar en la Fase 0.>

## Comandos

<Completar en la Fase 0. Todos deben funcionar en cmd desde la raíz del repo.>

## Estilo

- Identificadores de código en inglés; documentación, comentarios y mensajes de commit en español.
- Commits pequeños: `<feature>: tarea N — <qué>`; cambios de specs: `spec(<feature>): …` o `specs: …`.
- Andrés usa **cmd** (Símbolo del sistema), no PowerShell: continuación de línea con `^`, variables con `%VAR%`; en los ejemplos, `<valor>` es un marcador (en cmd no se escriben `<` ni `>`).
- Andrés está aprendiendo el dominio: al cerrar cada tarea, 2–4 líneas de qué se hizo y por qué, con un ejemplo si ayuda.
- Push y merge solo cuando Andrés lo pide (la skill `sdd-cerrar` lo pregunta).
