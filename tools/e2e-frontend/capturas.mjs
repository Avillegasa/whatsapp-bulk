// Capturas de la intranet con datos de ejemplo (F1.8b): /api/* lo responde este script, así se ve cada pantalla
// —con o sin pausa, claro u oscuro, escritorio o teléfono— sin base de datos ni sesión real. Para revisar el diseño.
// Uso (cmd, con "npx ng serve" corriendo en src\frontend):
//   node tools\e2e-frontend\capturas.mjs                → todas las pantallas, escritorio y 390 px, en %TEMP%\wabulk-capturas
//   node tools\e2e-frontend\capturas.mjs --oscuro --pausa
import { mkdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { Navegador } from './lib/navegador.mjs';

const OSCURO = process.argv.includes('--oscuro');
const salida = join(tmpdir(), 'wabulk-capturas');
mkdirSync(salida, { recursive: true });
let pausa = process.argv.includes('--pausa');
let sinSesion = false;

const hoy = new Date();
const iso = (horas) => new Date(hoy.getTime() + horas * 3600_000).toISOString().replace('Z', '').slice(0, 19);

const SESION = { autenticado: true, usuario: 'avillegas', roles: ['Operador', 'Aprobador', 'Admin'] };
const version = (n, extra = {}) => ({
  id: n, numero: n, plantillaId: 1, plantilla: 'promo_parrillera', idioma: 'es', categoria: 'MARKETING',
  formatoParametros: 'NAMED', listaId: 1, lista: 'Clientes mayoristas El Alto', destinatarios: 48, excluidos: 2,
  parametros: [{ nombre: 'nombre', fuente: 'NombreContacto', valor: 'cliente' }, { nombre: 'promocion', fuente: 'Fijo', valor: '15 % en cortes para parrilla' }],
  programadaUtc: iso(-2), expiraUtc: iso(30), hash: 'a3f9c1e07b5d42e8a1c6f0b9d3e7a2c4b8f1e6d0c9a7b3e5f2d4c6a8b0e1f3a5',
  creadaUtc: iso(-26), creadaPor: 'mlopez', enviadaAprobacionUtc: iso(-25), aprobadaUtc: iso(-24), aprobadaPor: 'avillegas',
  rechazadaUtc: null, rechazadaPor: null, motivoRechazo: null, ...extra,
});
const campana = (id, nombre, estado, extra = {}) => ({
  id, nombre, estado, estadoUtc: iso(-1), motivo: null, version: 1, plantilla: 'promo_parrillera', idioma: 'es',
  destinatarios: 48, programadaUtc: iso(-2), expiraUtc: iso(30), creadaPor: 'mlopez', creadaUtc: iso(-26), ...extra,
});
const DETALLE = (c, v) => ({
  campana: c, versiones: [v], envios: [
    { estado: 'Submitted', motivo: null, cantidad: 31 }, { estado: 'Queued', motivo: null, cantidad: 12 },
    { estado: 'Skipped', motivo: 'BajaEmpresa', cantidad: 3 }, { estado: 'Failed', motivo: null, cantidad: 2 }],
  encabezado: 'Fin de semana parrillero', cuerpo: 'Hola {{nombre}}, este sábado tenemos {{promocion}}. Te esperamos en nuestras sucursales.',
  pie: 'Responde BAJA para no recibir más', botones: ['Me interesa', 'No recibir más'],
});
const conversacion = (id, nombre, tel, texto, cons, puede, motivo, extra = {}) => ({
  contactoId: id, telefono: tel, nombre, tipo: 'text', texto, fechaUtc: iso(-id), ventanaHastaUtc: iso(24 - id),
  consentimiento: cons, puedeOfrecer: puede, motivo, ultimaOferta: null, respuesta: null, ...extra,
});
const contacto = (id, nombre, tel, cons, extra = {}) => ({
  id, telefono: tel, nombre, clienteId: 'C-' + (1000 + id), activo: true, consentimiento: cons, bajaPlataforma: false,
  ultimoMensajeUtc: iso(-id * 5), ...extra,
});
const plantilla = (id, nombre, estado, extra = {}) => ({
  id, metaId: '16968713686' + id, nombre, idioma: 'es', categoria: 'MARKETING', estado, estadoUtc: iso(-id * 3),
  motivo: null, ultimoEvento: null, calidad: estado === 'APPROVED' ? 'GREEN' : null, categoriaNueva: null,
  cambioCategoriaUtc: null, sincronizadaUtc: iso(-1), creadaPor: 'avillegas', ...extra,
});

function api(metodo, ruta) {
  const p = ruta.split('?')[0];
  if (p === '/api/auth/sesion') return sinSesion ? { autenticado: false, usuario: null, roles: [] } : SESION;
  if (p === '/api/monitor')
    return {
      pausa: pausa ? { enPausa: true, motivo: 'PlantillaPausada', desdeUtc: iso(-0.3), alertasAbiertas: 1 } : { enPausa: false, motivo: null, desdeUtc: null, alertasAbiertas: 0 },
      capacidad: { limite: 250, fuenteLimite: 'Meta (TIER_250)', presupuesto: 225, usados: 61, disponibles: 164 },
      campanias: [
        { campanaId: 1, nombre: 'Fin de semana parrillero', estado: 'Running', version: 2, total: 48, enCola: 12, enviandose: 1, enEspera: 0, enviados: 31, entregados: 29, leidos: 18, fallidos: 2, revision: 1, vencidos: 0, omitidos: { BajaEmpresa: 3, LimiteFrecuencia: 1 } },
        { campanaId: 3, nombre: 'Cortes premium diciembre', estado: 'Scheduled', version: 1, total: 120, enCola: 116, enviandose: 0, enEspera: 0, enviados: 0, entregados: 0, leidos: 0, fallidos: 0, revision: 0, vencidos: 0, omitidos: { SinConsentimiento: 4 } },
      ],
    };
  if (p === '/api/campanias')
    return [campana(1, 'Fin de semana parrillero', 'Running', { version: 2 }), campana(2, 'Embutidos navideños', 'PendingApproval', { creadaPor: 'mlopez' }),
      campana(3, 'Cortes premium diciembre', 'Scheduled', { destinatarios: 120 }), campana(4, 'Promo pollo entero', 'Draft'),
      campana(5, 'Día del padre', 'Cancelled', { motivo: 'Cambió la fecha del evento' })];
  if (p === '/api/campanias/opciones')
    return { plantillas: [{ id: 1, nombre: 'promo_parrillera', idioma: 'es', estado: 'APPROVED', parametros: ['nombre', 'promocion'] }], listas: [{ id: 1, nombre: 'Clientes mayoristas El Alto', miembros: 48 }] };
  if (p.startsWith('/api/campanias/')) return DETALLE(campana(1, 'Fin de semana parrillero', 'Running', { version: 2 }), version(2));
  if (p === '/api/aprobaciones') return [campana(2, 'Embutidos navideños', 'PendingApproval')];
  if (p.startsWith('/api/aprobaciones/')) return DETALLE(campana(2, 'Embutidos navideños', 'PendingApproval'), version(1, { aprobadaPor: null, aprobadaUtc: null }));
  if (p === '/api/mensajes')
    return [conversacion(1, 'Rosa Quispe', '+59171234567', 'Hola, ¿tienen costilla de res?', 'SinConsentimiento', true, null),
      conversacion(2, 'Carnicería San Miguel', '+59176543210', 'Quiero hacer un pedido para el sábado', 'OptIn', false, 'Ya acepta promociones', { respuesta: 'Si' }),
      conversacion(3, null, '+59170011223', '(image)', 'SinConsentimiento', false, 'Ya se le ofreció.', { ultimaOferta: 'Enviada' }),
      conversacion(4, 'Juan Mamani', '+59178889900', 'BAJA', 'OptOut', false, 'Pidió la baja')];
  if (p === '/api/contactos')
    return [contacto(1, 'Rosa Quispe', '+59171234567', 'SinConsentimiento'), contacto(2, 'Carnicería San Miguel', '+59176543210', 'OptIn'),
      contacto(3, 'Restaurante El Fogón', '+59172223344', 'OptIn'), contacto(4, 'Juan Mamani', '+59178889900', 'OptOut', { bajaPlataforma: true }),
      contacto(5, null, '+59170011223', 'NoContactar')];
  if (p.startsWith('/api/contactos/'))
    return { contacto: contacto(2, 'Carnicería San Miguel', '+59176543210', 'OptIn'), userId: null, listas: ['Clientes mayoristas El Alto'],
      historial: [{ recordedUtc: iso(-50), status: 'OptIn', source: 'Boton', evidence: 'Botón «Sí, quiero ofertas» · texto v1 · ofrecido por avillegas', recordedBy: 'webhook' }],
      mensajes: [{ id: 1, sentUtc: iso(-2), type: 'text', text: 'Quiero hacer un pedido para el sábado', buttonId: null }],
      ofertas: [{ id: 1, offeredUtc: iso(-51), offeredBy: 'avillegas', textVersion: 'v1', status: 'Enviada', response: 'Si', responseUtc: iso(-50) }] };
  if (p === '/api/listas')
    return [{ id: 1, nombre: 'Clientes mayoristas El Alto', descripcion: 'Carnicerías y restaurantes', miembros: 48, creadaUtc: iso(-200), creadaPor: 'mlopez' },
      { id: 2, nombre: 'Empleados (prueba)', descripcion: null, miembros: 5, creadaUtc: iso(-100), creadaPor: 'avillegas' }];
  if (p.startsWith('/api/listas/')) return [contacto(2, 'Carnicería San Miguel', '+59176543210', 'OptIn'), contacto(3, 'Restaurante El Fogón', '+59172223344', 'OptIn')];
  if (p === '/api/plantillas')
    return [plantilla(1, 'promo_parrillera', 'APPROVED'), plantilla(2, 'promo_demo', 'PENDING'), plantilla(3, 'promo_f15', 'REJECTED', { motivo: 'INVALID_FORMAT' }), plantilla(4, 'hello_world', 'APPROVED', { idioma: 'en_US', categoria: 'UTILITY' })];
  if (p === '/api/plantillas/botones') return { interes: 'Me interesa', baja: 'No recibir más' };
  if (p.startsWith('/api/plantillas/'))
    return { plantilla: plantilla(1, 'promo_parrillera', 'APPROVED'), formatoParametros: 'NAMED', encabezado: 'Fin de semana parrillero',
      cuerpo: 'Hola {{nombre}}, este sábado tenemos {{promocion}}.', pie: 'Responde BAJA para no recibir más', botones: ['Me interesa', 'No recibir más'], parametros: ['nombre', 'promocion'] };
  if (p === '/api/bajas')
    return [{ telefono: '+59178889900', nombre: 'Juan Mamani', capa: 'Empresa', estado: 'OptOut', fuente: 'PalabraClave', desdeUtc: iso(-30), evidencia: 'Mensaje «BAJA»' },
      { telefono: '+59178889900', nombre: 'Juan Mamani', capa: 'Plataforma', estado: 'OptOut', fuente: 'user_preferences', desdeUtc: iso(-29), evidencia: null }];
  if (p === '/api/revision')
    return [{ trabajoId: 11, campanaId: 1, campana: 'Fin de semana parrillero', telefono: '+59170000001', nombre: 'Ana Choque', intentoId: 'x', inicioUtc: iso(-3), vencido: true, detalle: null }];
  if (p === '/api/alertas')
    return { pausa: pausa ? { enPausa: true, motivo: 'PlantillaPausada', desdeUtc: iso(-0.3), alertasAbiertas: 1 } : { enPausa: false, motivo: null, desdeUtc: null, alertasAbiertas: 0 },
      abiertas: pausa ? [{ id: 1, tipo: 'PlantillaPausada', clave: 'promo_parrillera', mensaje: 'Meta pausó promo_parrillera', detalle: null, origen: 'Webhook', ocurrencias: 1, creadaUtc: iso(-0.3), ultimaUtc: iso(-0.3) }] : [] };
  if (p === '/api/usuarios')
    return [{ id: 'a', usuario: 'avillegas', roles: ['Admin', 'Aprobador', 'Operador'], activo: true, bloqueadoHasta: null },
      { id: 'b', usuario: 'mlopez', roles: ['Operador'], activo: true, bloqueadoHasta: null },
      { id: 'c', usuario: 'prueba1', roles: ['Operador'], activo: false, bloqueadoHasta: null }];
  return null;
}


const RUTAS = ['/', '/mensajes', '/contactos', '/importar', '/listas', '/plantillas', '/campanias', '/monitor', '/revision', '/bajas', '/aprobaciones', '/alertas', '/usuarios'];
const DETALLES = {
  '/campanias': "document.querySelector('tbody button')?.click()",
  '/aprobaciones': "document.querySelector('tbody button')?.click()",
  '/contactos': "document.querySelectorAll('tbody button')[1]?.click()",
  '/plantillas': "document.querySelector('tbody button')?.click()",
};
const nav = await Navegador.abrir({ perfil: join(salida, '.perfil'), puerto: 9336, base: 'http://localhost:4200', interceptarApi: (m, r) => api(m, r) });
await nav.temaSistema(OSCURO);
for (const ancho of [1366, 390]) {
  await nav.ancho(ancho);
  sinSesion = true;
  await nav.ir('/login');
  console.log(await nav.captura(salida, `login-${ancho}`));
  sinSesion = false;
  for (const ruta of RUTAS) {
    await nav.ir(ruta);
    await new Promise((r) => setTimeout(r, 1200));
    if (DETALLES[ruta]) {
      await nav.eval(DETALLES[ruta]);
      await new Promise((r) => setTimeout(r, 800));
    }
    const nombre = (ruta === '/' ? 'inicio' : ruta.slice(1)) + `-${ancho}`;
    const desborde = await nav.eval('document.documentElement.scrollWidth');
    console.log(await nav.captura(salida, nombre), desborde > ancho ? `DESBORDE ${desborde}px` : '');
  }
}
await nav.cerrar();
process.exit(0);
