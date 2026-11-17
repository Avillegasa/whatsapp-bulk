import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService, Rol } from './auth.service';

/** Rutas que exigen sesión: sin sesión → /login, recordando a dónde se quería ir. */
export const sesionGuard: CanActivateFn = (_route, state) => {
  const auth = inject(AuthService);
  return auth.autenticado()
    ? true
    : inject(Router).createUrlTree(['/login'], { queryParams: { volver: state.url } });
};

/**
 * Rutas por rol: `data: { roles: ['Admin'] }`. Es solo comodidad de la interfaz; quien protege de verdad es la Api
 * (política con nombre en cada endpoint), que responde 403 aunque alguien escriba la URL a mano.
 */
export const rolGuard: CanActivateFn = (route) => {
  const roles = (route.data['roles'] ?? []) as Rol[];
  return inject(AuthService).tieneAlgunRol(roles) ? true : inject(Router).createUrlTree(['/sin-permiso']);
};

/** /login con sesión abierta → inicio. */
export const invitadoGuard: CanActivateFn = () =>
  inject(AuthService).autenticado() ? inject(Router).createUrlTree(['/']) : true;
