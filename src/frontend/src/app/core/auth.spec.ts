import { provideHttpClient, withInterceptors, withXsrfConfiguration } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, provideRouter, Router, RouterStateSnapshot, UrlTree } from '@angular/router';
import { rolGuard, sesionGuard } from './auth.guards';
import { AuthService, Sesion } from './auth.service';
import { sesionInterceptor } from './sesion.interceptor';

@Component({ template: '' })
class Vacio {}

const ADMIN: Sesion = { autenticado: true, usuario: 'andres', roles: ['Admin'] };
const OPERADOR: Sesion = { autenticado: true, usuario: 'ope', roles: ['Operador'] };

describe('AuthService y guardias', () => {
  let auth: AuthService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'login', component: Vacio }]),
        provideHttpClient(
          withInterceptors([sesionInterceptor]),
          withXsrfConfiguration({ cookieName: 'XSRF-TOKEN', headerName: 'X-XSRF-TOKEN' }),
        ),
        provideHttpClientTesting(),
      ],
    });
    auth = TestBed.inject(AuthService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
    document.cookie = 'XSRF-TOKEN=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/';
  });

  async function login(sesion: Sesion): Promise<void> {
    const promesa = auth.login(sesion.usuario!, 'clave');
    http.expectOne('/api/auth/login').flush(sesion);
    expect(await promesa).toBeNull();
  }

  it('el login copia la cookie XSRF-TOKEN al encabezado X-XSRF-TOKEN', async () => {
    document.cookie = 'XSRF-TOKEN=token-de-prueba; path=/';

    const promesa = auth.login('andres', 'clave');
    const req = http.expectOne('/api/auth/login');
    expect(req.request.method).toBe('POST');
    expect(req.request.headers.get('X-XSRF-TOKEN')).toBe('token-de-prueba');
    req.flush(ADMIN);

    expect(await promesa).toBeNull();
    expect(auth.autenticado()).toBe(true);
    expect(auth.roles()).toEqual(['Admin']);
  });

  it('usuario bloqueado: devuelve el mensaje de la Api y no abre sesión', async () => {
    const promesa = auth.login('andres', 'mala');
    http
      .expectOne('/api/auth/login')
      .flush({ error: 'Bloqueado', mensaje: 'Usuario bloqueado' }, { status: 401, statusText: 'Unauthorized' });

    expect((await promesa)?.error).toBe('Bloqueado');
    expect(auth.autenticado()).toBe(false);
  });

  it('sin sesión, la guardia manda al login recordando la ruta', () => {
    const resultado = TestBed.runInInjectionContext(() =>
      sesionGuard({} as ActivatedRouteSnapshot, { url: '/usuarios' } as RouterStateSnapshot),
    );

    expect(TestBed.inject(Router).serializeUrl(resultado as UrlTree)).toBe('/login?volver=%2Fusuarios');
  });

  it('Operador en una ruta de Admin → sin-permiso; Admin pasa', async () => {
    const ruta = { data: { roles: ['Admin'] } } as unknown as ActivatedRouteSnapshot;
    const estado = {} as RouterStateSnapshot;
    await login(OPERADOR);
    const operador = TestBed.runInInjectionContext(() => rolGuard(ruta, estado));
    expect(TestBed.inject(Router).serializeUrl(operador as UrlTree)).toBe('/sin-permiso');

    await login(ADMIN);
    expect(TestBed.runInInjectionContext(() => rolGuard(ruta, estado))).toBe(true);
  });

  it('un 401 en otra llamada cierra la sesión local', async () => {
    await login(ADMIN);

    auth['http'].get('/api/usuarios').subscribe({ error: () => undefined });
    http.expectOne('/api/usuarios').flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(auth.autenticado()).toBe(false);
  });
});
