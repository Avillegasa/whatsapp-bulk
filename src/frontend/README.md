# Frontend — WhatsApp Bulk

Angular 21 (standalone, signals, sin zone.js). Pruebas con Vitest (`ng test`).

- Instalar: `npm ci`
- Desarrollo: `npx ng serve` → http://localhost:4200. El proxy (`proxy.conf.json`) manda `/api` a la Api en
  http://localhost:5047, así la app y la Api son el mismo origen y Angular envía el encabezado XSRF.
- Compilar: `npx ng build` · Pruebas: `npx ng test --watch=false`

Estructura: `core/` (sesión, guardias, interceptor, menú por rol), `layout/` (marco con menú), `pages/` (pantallas).
