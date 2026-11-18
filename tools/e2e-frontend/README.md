# Prueba de punta a punta de la intranet (F1.8b)

Dos herramientas en Node (sin dependencias: Node 22+ y Edge o Chrome instalados).

## `e2e.mjs`: la app real, de punta a punta

Levanta la app completa contra una base **temporal** y un **Meta falso**:

- Una base `WaBulk_E2E_<fecha>` con todas las migraciones. Se borra al final. La base `WaBulk` de desarrollo no se toca.
- Api (puerto 5097), receptor de webhooks (5098) y Worker con el despachador encendido, compilados en `%TEMP%\wabulk-e2e\build`. Así no bloquean la Api ni el Worker que tengas corriendo.
- Un Meta falso (5099): `Meta:BaseUrl` apunta ahí, así que **ningún mensaje sale a WhatsApp**.
- El build de producción de Angular, servido en http://127.0.0.1:4300 con proxy de `/api` y websockets, como lo hará nginx.
- Edge sin ventana, que hace de persona: escribe, hace clic, elige archivos y usa el teclado.

Recorrido (cada paso verifica lo que se ve en pantalla):

1. **Login y marco:**
   - Login con clave mala y luego con la buena.
   - Alta de usuarios, con el error por campo.
   - Tema Sistema/Claro/Oscuro y «Saltar al contenido» con el teclado.
   - Salir y volver a una pantalla protegida.
2. **Consentimiento:**
   - Llega «Hola» por webhook firmado y aparece en Mensajes recientes con la hora local.
   - El diálogo de «Ofrecer promociones» se cancela con Escape sin enviar nada.
   - «Enviar pregunta» manda un solo mensaje a Meta.
   - El cliente toca «Sí, quiero ofertas» (webhook) y queda «Acepta promociones».
3. **Listas, importación y contactos:**
   - Crear una lista.
   - Importar un CSV con 4 filas (2 OptIn, 1 baja, 1 inválida).
   - Agregar a Ana a la lista y abrir su historial.
4. **Plantillas y campaña con cuatro ojos:**
   - Sincronizar con Meta y crear una plantilla con la vista previa al lado.
   - Campaña: errores por campo, borrador y Enviar a aprobación.
   - Otra persona (Aprobador) no ve Contactos y aprueba la versión.
5. **Despacho y monitor:**
   - El despachador envía 3 (la baja queda omitida) y el Monitor «En vivo» los muestra.
   - Webhooks delivered/read: los contadores cambian sin recargar en menos de 5 s.
   - Plantilla PAUSED: aparece el banner EN PAUSA.
   - El Aprobador reanuda desde Alertas.
   - Baja manual, que aparece en Bajas.
6. **Celular (390 px):** ninguna pantalla se desborda; el menú abre y cierra.
7. **Administración:** desactivar un usuario con el diálogo.
8. **Controles finales:**
   - Sin errores de JavaScript ni respuestas 5xx.
   - Puerta «cero duplicados» en la base.
   - Cada acción quedó en `AuditLog` con su usuario.

Uso (cmd, desde la raíz del repo, con SQL Server levantado):

```
node tools\e2e-frontend\e2e.mjs
node tools\e2e-frontend\e2e.mjs --sin-compilar
node tools\e2e-frontend\e2e.mjs --mantener
```

- `--sin-compilar` reutiliza la última compilación (más rápido).
- `--mantener` deja la base y los procesos al final para mirar a mano en http://127.0.0.1:4300. Las claves de los usuarios de prueba quedan en `usuarios.json` de la carpeta de salida. Ctrl+C para terminar.

El informe (`informe.json`), los logs de cada proceso y las capturas (incluida la del primer paso que falle) quedan en `%TEMP%\wabulk-e2e\<fecha>\`. El script termina con código 0 si todo pasó.

La clave de SQL Server se lee de `.env` y no se muestra. Los usuarios de prueba (`e2e-admin`, `e2e-operador`, `e2e-aprobador`) tienen claves al azar que solo viven en la corrida.

## `capturas.mjs`: revisión visual con datos de ejemplo

Responde `/api/*` con datos de ejemplo, así que no hace falta ni base ni sesión. Saca una captura de cada pantalla en escritorio (1366 px) y en celular (390 px), y avisa si alguna se desborda a lo ancho. Necesita `npx ng serve` corriendo.

```
node tools\e2e-frontend\capturas.mjs
node tools\e2e-frontend\capturas.mjs --oscuro --pausa
```

Las capturas quedan en `%TEMP%\wabulk-capturas\`.
