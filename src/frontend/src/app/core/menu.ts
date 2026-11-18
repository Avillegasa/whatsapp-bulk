import { Rol } from './auth.service';

export interface OpcionMenu {
  ruta: string;
  texto: string;
  /** Vacío = todos los usuarios con sesión. */
  roles: Rol[];
  /** Título del grupo en el menú lateral (F1.8b); las opciones de un grupo van seguidas. */
  grupo?: string;
}

/** Menú por rol. Cada fase suma sus pantallas aquí (contactos F1.4, plantillas F1.5, campañas F1.6…). */
export const MENU: OpcionMenu[] = [
  { ruta: '/', texto: 'Inicio', roles: [] },
  { ruta: '/mensajes', texto: 'Mensajes recientes', roles: ['Operador'], grupo: 'Clientes' },
  { ruta: '/contactos', texto: 'Contactos', roles: ['Operador'], grupo: 'Clientes' },
  { ruta: '/importar', texto: 'Importar', roles: ['Operador'], grupo: 'Clientes' },
  { ruta: '/listas', texto: 'Listas', roles: ['Operador'], grupo: 'Clientes' },
  { ruta: '/plantillas', texto: 'Plantillas', roles: ['Operador'], grupo: 'Envíos' },
  { ruta: '/campanias', texto: 'Campañas', roles: ['Operador'], grupo: 'Envíos' },
  { ruta: '/monitor', texto: 'Monitor', roles: ['Operador'], grupo: 'Envíos' },
  { ruta: '/revision', texto: 'Envíos inciertos', roles: ['Operador'], grupo: 'Envíos' },
  { ruta: '/bajas', texto: 'Bajas', roles: ['Operador'], grupo: 'Envíos' },
  { ruta: '/aprobaciones', texto: 'Aprobaciones', roles: ['Aprobador'], grupo: 'Aprobación' },
  { ruta: '/alertas', texto: 'Alertas', roles: ['Aprobador'], grupo: 'Aprobación' },
  { ruta: '/usuarios', texto: 'Usuarios', roles: ['Admin'], grupo: 'Administración' },
];
