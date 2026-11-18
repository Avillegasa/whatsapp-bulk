import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuthService, Sesion } from '../core/auth.service';
import { Shell } from './shell';

function menuPara(sesion: Sesion): string[] {
  TestBed.configureTestingModule({
    imports: [Shell],
    providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
  });
  (TestBed.inject(AuthService) as unknown as { sesion: { set(s: Sesion): void } }).sesion.set(
    sesion,
  );
  const fixture = TestBed.createComponent(Shell);
  fixture.detectChanges();
  const enlaces = (fixture.nativeElement as HTMLElement).querySelectorAll('nav a');
  return Array.from(enlaces, (a) => a.textContent?.trim() ?? '');
}

describe('Shell: menú por rol', () => {
  it('Admin ve Usuarios', () => {
    expect(menuPara({ autenticado: true, usuario: 'andres', roles: ['Admin'] })).toEqual([
      'Inicio',
      'Usuarios',
    ]);
  });

  it('Operador ve las pantallas de contactos (F1.4) y plantillas (F1.5), y no Usuarios', () => {
    expect(menuPara({ autenticado: true, usuario: 'x', roles: ['Operador', 'Aprobador'] })).toEqual(
      [
        'Inicio',
        'Mensajes recientes',
        'Contactos',
        'Importar',
        'Listas',
        'Plantillas',
        'Campañas',
        'Monitor',
        'Envíos inciertos',
        'Bajas',
        'Aprobaciones',
        'Alertas',
      ],
    );
  });

  it('Aprobador solo no ve contactos ni Usuarios', () => {
    expect(menuPara({ autenticado: true, usuario: 'x', roles: ['Aprobador'] })).toEqual([
      'Inicio',
      'Aprobaciones',
      'Alertas',
    ]);
  });
});
