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
