import { Rol } from './auth.service';

export interface OpcionMenu {
  ruta: string;
  texto: string;
  /** Vacío = todos los usuarios con sesión. */
  roles: Rol[];
}

/** Menú por rol. Cada fase suma sus pantallas aquí (contactos F1.4, plantillas F1.5, campañas F1.6…). */
export const MENU: OpcionMenu[] = [
  { ruta: '/', texto: 'Inicio', roles: [] },
  { ruta: '/mensajes', texto: 'Mensajes recientes', roles: ['Operador'] },
  { ruta: '/contactos', texto: 'Contactos', roles: ['Operador'] },
  { ruta: '/importar', texto: 'Importar', roles: ['Operador'] },
  { ruta: '/listas', texto: 'Listas', roles: ['Operador'] },
  { ruta: '/plantillas', texto: 'Plantillas', roles: ['Operador'] },
  { ruta: '/usuarios', texto: 'Usuarios', roles: ['Admin'] },
];
