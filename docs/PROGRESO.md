# Progreso

**Fase actual:** Fase 0 — PoC
**Siguiente tarea:** F0.2 (Tú) — en Claude desktop: "Sigamos con WhatsApp"
**Última actualización:** 2026-11-16

## Estado de tareas

Estados: Pendiente · En curso · Hecho (sin verificar) · Verificado · Bloqueado

| ID | Tarea | Quién | Estado | Evidencia |
| --- | --- | --- | --- | --- |
| F0.1 | App de Meta y número de prueba | Tú | Verificado | `hello_world` recibido (confirmación de Andrés); App, WABA, Phone Number y System User IDs en META-SETUP.md; token del System User (sin caducidad) en gestor de contraseñas; grep `EAA…` sin resultados en la carpeta |
| F0.2 | Repositorio en GitHub | Tú | En curso | |
| F0.3 | Esqueleto de la solución .NET | Claude Code | Pendiente | |
| F0.4 | SQL Server local y migración inicial | Claude Code | Pendiente | |
| F0.5 | Cliente de Meta y envío con SendAttempt | Claude Code + Tú | Pendiente | |
| F0.6 | Receptor de webhooks | Claude Code + Tú | Pendiente | |
| F0.7 | Procesador de eventos | Claude Code | Pendiente | |
| F0.8 | Herramienta de replay firmado | Claude Code | Pendiente | |
| F0.9 | Despachador con cola y lease | Claude Code | Pendiente | |
| F0.10 | Bajas | Claude Code | Pendiente | |
| F0.11 | Concurrencia y capacidad | Claude Code | Pendiente | |
| F0.12 | Alertas simuladas | Claude Code | Pendiente | |

## Pendientes y bloqueos

- Definir dónde corre SQL Server en producción (D-006).
- Crear el portafolio de Meta de la empresa antes de F1.1 (D-015).
- Confirmar con el área comercial o legal cómo consta el consentimiento de los clientes para promociones (antes de la Fase 2).

## Bitácora (lo más reciente arriba)

### 2026-11-16 — F0.2 iniciada (Claude desktop)
- Estado: repo privado https://github.com/Avillegasa/whatsapp-bulk con commit 8176d15 (solo README) en `main`; rama local `dev`. Clon en `C:\Users\avillegas\dev\whatsapp-bulk`.
- Decisión: D-016 — el clon pasa a ser la carpeta del proyecto.
- Quedó en: copiar los archivos de `Documents\Whatsapp-bulk-proy` al clon, commit y push.

### 2026-11-16 — F0.1: portafolio de prueba (Claude desktop)
- Decisión: D-015 — el PoC usa un portafolio propio de prueba; la empresa aún no tiene uno.
- Hecho: app `WaBulk PoC` (App ID 1669296924583840) en el portafolio `WaBulk Preubas`.
- Hecho: número de prueba +1 (555) 189-2694, Phone Number ID 1524289747428097, Messaging account ID 2088365445132575 (anotados en META-SETUP.md).
- Hecho: `hello_world` recibido en el teléfono de Andrés (confirmación de Andrés, 12:09). Sin tokens en archivos (grep `EAA…`).
- Hecho: System User `wabulk-poc` creado con acceso total a la cuenta de WhatsApp 2043065989689955 (captura de Andrés); IDs de portafolio, WABA y System User anotados.
- Hecho: token de `wabulk-poc` generado (whatsapp_business_messaging, whatsapp_business_management; caducidad: nunca) y guardado en el gestor de contraseñas de Andrés. `business_management` no estaba disponible en el panel.
- Verificación: F0.1 → Verificado.
- Siguiente: F0.2 — repositorio en GitHub (la carpeta aún no es repo git).

### 2026-11-16 — F0.1 iniciada (Claude desktop)
- Hecho: instrucciones del proyecto de claude.ai configuradas; guía `guia-del-proyecto.md` en el proyecto; comandos para cmd.
- Quedó en: F0.1 Paso 1 — confirmar acceso de Andrés al portafolio de Meta de la empresa (nombre del portafolio y rol).
- Siguiente: continuar F0.1 desde el Paso 1.

### 2026-11-16 — Setup inicial (Claude desktop)
- Hecho: estructura de carpetas, `CLAUDE.md`, `docs/` (arquitectura, plan, progreso, decisiones, setup de Meta), skills `wa-sesion` y `wa-verificar`, `.gitignore`, `.env.example`.
- Decisiones: D-001 a D-014.
- Verificación: archivos presentes en la carpeta del proyecto.
- Siguiente: F0.1 y F0.2 (Tú), pueden hacerse en paralelo.
