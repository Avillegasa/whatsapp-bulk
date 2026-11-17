import { provideHttpClient, withInterceptors, withXsrfConfiguration } from '@angular/common/http';
import {
  ApplicationConfig,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
} from '@angular/core';
import { provideRouter } from '@angular/router';

import { routes } from './app.routes';
import { AuthService } from './core/auth.service';
import { sesionInterceptor } from './core/sesion.interceptor';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideHttpClient(
      withInterceptors([sesionInterceptor]),
      // Los valores por defecto de Angular, escritos explícitos porque deben coincidir con la Api.
      withXsrfConfiguration({ cookieName: 'XSRF-TOKEN', headerName: 'X-XSRF-TOKEN' }),
    ),
    // Antes de la primera navegación: saber si hay sesión (las guardias la necesitan) y obtener la cookie XSRF.
    provideAppInitializer(() => inject(AuthService).cargarSesion()),
  ],
};
