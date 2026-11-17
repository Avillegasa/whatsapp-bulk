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
  (TestBed.inject(AuthService) as unknown as { sesion: { set(s: Sesion): void } }).sesion.set(sesion);
  const fixture = TestBed.createComponent(Shell);
  fixture.detectChanges();
  const enlaces = (fixture.nativeElement as HTMLElement).querySelectorAll('nav a');
  return Array.from(enlaces, (a) => a.textContent?.trim() ?? '');
}

describe('Shell: menú por rol', () => {
  it('Admin ve Usuarios', () => {
    expect(menuPara({ autenticado: true, usuario: 'andres', roles: ['Admin'] })).toEqual(['Inicio', 'Usuarios']);
  });

  it('Operador y Aprobador no ven Usuarios', () => {
    expect(menuPara({ autenticado: true, usuario: 'x', roles: ['Operador', 'Aprobador'] })).toEqual(['Inicio']);
  });
});
