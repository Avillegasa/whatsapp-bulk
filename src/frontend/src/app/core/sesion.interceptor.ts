import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';
import { AuthService } from './auth.service';

/** Un 401 fuera de /api/auth/* significa que la sesión venció: se limpia el estado y se vuelve al login. */
export const sesionInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  return next(req).pipe(
    catchError((err: unknown) => {
      if (err instanceof HttpErrorResponse && err.status === 401 && !req.url.startsWith('/api/auth/')) {
        auth.sesionPerdida();
        void router.navigate(['/login'], { queryParams: { volver: router.url } });
      }
      return throwError(() => err);
    }),
  );
};
