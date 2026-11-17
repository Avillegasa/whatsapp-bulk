import { DatePipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { catchError, of } from 'rxjs';

interface UsuarioResumen {
  usuario: string;
  roles: string[];
  bloqueadoHasta: string | null;
}

/** Lista de usuarios (solo Admin). Crear, desactivar, cambiar rol y restablecer clave: F1.3. */
@Component({
  selector: 'app-usuarios',
  imports: [DatePipe],
  template: `
    <h1>Usuarios</h1>
    @if (error()) {
      <p class="error" role="alert">{{ error() }}</p>
    } @else if (usuarios(); as lista) {
      <table>
        <thead>
          <tr><th>Usuario</th><th>Roles</th><th>Bloqueado hasta</th></tr>
        </thead>
        <tbody>
          @for (u of lista; track u.usuario) {
            <tr>
              <td>{{ u.usuario }}</td>
              <td>{{ u.roles.join(', ') }}</td>
              <td>{{ esFuturo(u.bloqueadoHasta) ? (u.bloqueadoHasta | date: 'dd/MM/yyyy HH:mm') : '—' }}</td>
            </tr>
          }
        </tbody>
      </table>
    } @else {
      <p>Cargando…</p>
    }
  `,
  styles: `
    table { border-collapse: collapse; width: 100%; }
    th, td { text-align: left; padding: 0.5rem; border-bottom: 1px solid #ddd; }
    .error { color: #b3261e; }
  `,
})
export class Usuarios {
  protected readonly error = signal<string | null>(null);
  protected readonly usuarios = toSignal(
    inject(HttpClient)
      .get<UsuarioResumen[]>('/api/usuarios')
      .pipe(
        catchError(() => {
          this.error.set('No se pudo cargar la lista de usuarios.');
          return of(null);
        }),
      ),
  );

  protected esFuturo(fecha: string | null): boolean {
    return fecha !== null && new Date(fecha).getTime() > Date.now();
  }
}
