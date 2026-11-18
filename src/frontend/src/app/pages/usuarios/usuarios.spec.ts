import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuthService, Sesion } from '../../core/auth.service';
import { Usuario } from './usuarios.service';
import { Usuarios } from './usuarios';
import { ConfirmacionService } from '../../core/confirmacion';

const ANDRES: Usuario = { id: '1', usuario: 'andres', roles: ['Admin'], activo: true, bloqueadoHasta: null };
const ANA: Usuario = { id: '2', usuario: 'ana', roles: ['Operador'], activo: true, bloqueadoHasta: null };
const FUTURO = new Date(Date.now() + 10 * 60_000).toISOString();

describe('Pantalla de usuarios', () => {
  let fixture: ComponentFixture<Usuarios>;
  let http: HttpTestingController;
  let el: HTMLElement;

  async function abrir(lista: Usuario[]): Promise<void> {
    TestBed.configureTestingModule({
      imports: [Usuarios],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    const sesion: Sesion = { autenticado: true, usuario: 'andres', roles: ['Admin'] };
    (TestBed.inject(AuthService) as unknown as { sesion: { set(s: Sesion): void } }).sesion.set(sesion);
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(Usuarios);
    el = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
    http.expectOne('/api/usuarios').flush(lista);
    await estable();
  }

  /** Responde la acción y la recarga de la lista que viene después. */
  async function responder(url: string, metodo: string, cuerpo: Usuario, lista: Usuario[]): Promise<unknown> {
    const req = http.expectOne((r) => r.url === url && r.method === metodo);
    const enviado = req.request.body;
    req.flush(cuerpo);
    await estable();
    http.expectOne('/api/usuarios').flush(lista);
    await estable();
    return enviado;
  }

  function boton(fila: number, texto: string): HTMLButtonElement {
    const filas = el.querySelectorAll('tbody tr');
    const b = Array.from(filas[fila].querySelectorAll('button')).find((x) => x.textContent?.trim() === texto);
    if (!b) throw new Error(`No hay botón "${texto}" en la fila ${fila}`);
    return b;
  }

  /** Deja correr las promesas pendientes (await de la Api) y vuelve a pintar. */
  async function estable(): Promise<void> {
    // Sin zone.js, whenStable() no espera a los await del componente: se cede el turno y se repinta a mano.
    await new Promise((r) => setTimeout(r, 0));
    fixture.detectChanges();
  }

  afterEach(() => http.verify());

  it('lista usuarios y no ofrece desactivarse a uno mismo', async () => {
    await abrir([ANDRES, ANA]);

    const filas = el.querySelectorAll('tbody tr');
    expect(filas.length).toBe(2);
    expect(filas[0].textContent).toContain('(tú)');
    expect(Array.from(filas[0].querySelectorAll('button')).map((b) => b.textContent?.trim())).not.toContain('Desactivar');
    expect(boton(1, 'Desactivar')).toBeTruthy();
  });

  it('cambiar roles: aparece "Guardar roles" y envía los roles marcados', async () => {
    await abrir([ANDRES, ANA]);
    const aprobador = el.querySelector<HTMLInputElement>('input[aria-label="Aprobador de ana"]')!;

    aprobador.click();
    await estable();
    boton(1, 'Guardar roles').click();
    const enviado = await responder('/api/usuarios/2/roles', 'PUT', { ...ANA, roles: ['Operador', 'Aprobador'] }, [ANDRES, ANA]);

    expect(enviado).toEqual({ roles: ['Operador', 'Aprobador'] });
    expect(el.querySelector('.aviso')?.textContent).toContain('Roles de ana actualizados');
  });

  it('desactivar pide confirmación y llama a la Api', async () => {
    await abrir([ANDRES, ANA]);
    const confirmar = vi.spyOn(TestBed.inject(ConfirmacionService), 'preguntar').mockResolvedValue(true);

    boton(1, 'Desactivar').click();
    await new Promise((r) => setTimeout(r, 0)); // la confirmación es asíncrona (diálogo propio, F1.8b)
    await responder('/api/usuarios/2/desactivar', 'POST', { ...ANA, activo: false }, [ANDRES, { ...ANA, activo: false }]);

    expect(confirmar).toHaveBeenCalled();
    expect(el.querySelectorAll('tbody tr')[1].textContent).toContain('Desactivado');
    expect(boton(1, 'Activar')).toBeTruthy();
  });

  it('usuario bloqueado: muestra hasta cuándo y el botón Desbloquear', async () => {
    await abrir([ANDRES, { ...ANA, bloqueadoHasta: FUTURO }]);

    expect(el.querySelectorAll('tbody tr')[1].textContent).toContain('Bloqueado hasta');
    boton(1, 'Desbloquear').click();
    await responder('/api/usuarios/2/desbloquear', 'POST', ANA, [ANDRES, ANA]);

    expect(el.querySelectorAll('tbody tr')[1].textContent).not.toContain('Bloqueado hasta');
  });

  it('error de la Api: muestra su mensaje en español', async () => {
    await abrir([ANDRES, ANA]);
    boton(1, 'Restablecer clave').click();
    await estable();
    const input = el.querySelector<HTMLInputElement>('form.clave input')!;
    input.value = 'corta';
    input.dispatchEvent(new Event('input'));
    await estable();

    el.querySelector<HTMLButtonElement>('form.clave button')!.click();
    await estable();
    http
      .expectOne('/api/usuarios/2/clave')
      .flush(
        { error: 'PasswordTooShort', mensaje: 'La clave debe tener al menos 10 caracteres.' },
        { status: 400, statusText: 'Bad Request' },
      );
    await estable();

    expect(el.querySelector('.aviso.error')?.textContent).toContain('al menos 10 caracteres');
  });
});
