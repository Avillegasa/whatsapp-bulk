import { Routes } from '@angular/router';
import { invitadoGuard, rolGuard, sesionGuard } from './core/auth.guards';
import { Shell } from './layout/shell';
import { Login } from './pages/login/login';

export const routes: Routes = [
  { path: 'login', component: Login, canActivate: [invitadoGuard], title: 'Iniciar sesión · WhatsApp Bulk' },
  {
    path: '',
    component: Shell,
    canActivate: [sesionGuard],
    children: [
      {
        path: '',
        loadComponent: () => import('./pages/inicio/inicio').then((m) => m.Inicio),
        title: 'Inicio · WhatsApp Bulk',
      },
      {
        path: 'mensajes',
        canActivate: [rolGuard],
        data: { roles: ['Operador'] },
        loadComponent: () => import('./pages/mensajes/mensajes').then((m) => m.Mensajes),
        title: 'Mensajes recientes · WhatsApp Bulk',
      },
      {
        path: 'contactos',
        canActivate: [rolGuard],
        data: { roles: ['Operador'] },
        loadComponent: () => import('./pages/contactos/contactos').then((m) => m.Contactos),
        title: 'Contactos · WhatsApp Bulk',
      },
      {
        path: 'importar',
        canActivate: [rolGuard],
        data: { roles: ['Operador'] },
        loadComponent: () => import('./pages/importar/importar').then((m) => m.Importar),
        title: 'Importar contactos · WhatsApp Bulk',
      },
      {
        path: 'listas',
        canActivate: [rolGuard],
        data: { roles: ['Operador'] },
        loadComponent: () => import('./pages/listas/listas').then((m) => m.Listas),
        title: 'Listas · WhatsApp Bulk',
      },
      {
        path: 'plantillas',
        canActivate: [rolGuard],
        data: { roles: ['Operador'] },
        loadComponent: () => import('./pages/plantillas/plantillas').then((m) => m.Plantillas),
        title: 'Plantillas · WhatsApp Bulk',
      },
      {
        path: 'campanias',
        canActivate: [rolGuard],
        data: { roles: ['Operador'] },
        loadComponent: () => import('./pages/campanias/campanias').then((m) => m.Campanias),
        title: 'Campañas · WhatsApp Bulk',
      },
      {
        path: 'aprobaciones',
        canActivate: [rolGuard],
        data: { roles: ['Aprobador'] },
        loadComponent: () => import('./pages/aprobaciones/aprobaciones').then((m) => m.Aprobaciones),
        title: 'Aprobaciones · WhatsApp Bulk',
      },
      {
        path: 'usuarios',
        canActivate: [rolGuard],
        data: { roles: ['Admin'] },
        loadComponent: () => import('./pages/usuarios/usuarios').then((m) => m.Usuarios),
        title: 'Usuarios · WhatsApp Bulk',
      },
      {
        path: 'sin-permiso',
        loadComponent: () => import('./pages/sin-permiso/sin-permiso').then((m) => m.SinPermiso),
        title: 'Sin permiso · WhatsApp Bulk',
      },
    ],
  },
  { path: '**', redirectTo: '' },
];
