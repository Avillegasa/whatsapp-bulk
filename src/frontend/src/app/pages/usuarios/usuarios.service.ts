import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom, Observable } from 'rxjs';
import { Rol } from '../../core/auth.service';

export interface Usuario {
  id: string;
  usuario: string;
  roles: Rol[];
  activo: boolean;
  bloqueadoHasta: string | null;
}

/** Resultado de una acción: el usuario actualizado o el mensaje de error de la Api (ya en español). */
export type Resultado = { ok: true; usuario: Usuario } | { ok: false; mensaje: string };

/** Llamadas a /api/usuarios (solo Admin). Cada cambio queda en AuditLog del lado de la Api. */
@Injectable({ providedIn: 'root' })
export class UsuariosService {
  private readonly http = inject(HttpClient);

  listar(): Promise<Usuario[]> {
    return firstValueFrom(this.http.get<Usuario[]>('/api/usuarios'));
  }

  crear(usuario: string, roles: Rol[], claveTemporal: string): Promise<Resultado> {
    return this.ejecutar(this.http.post<Usuario>('/api/usuarios', { usuario, roles, claveTemporal }));
  }

  cambiarRoles(id: string, roles: Rol[]): Promise<Resultado> {
    return this.ejecutar(this.http.put<Usuario>(`/api/usuarios/${encodeURIComponent(id)}/roles`, { roles }));
  }

  activar(id: string, activo: boolean): Promise<Resultado> {
    const accion = activo ? 'activar' : 'desactivar';
    return this.ejecutar(this.http.post<Usuario>(`/api/usuarios/${encodeURIComponent(id)}/${accion}`, null));
  }

  restablecerClave(id: string, claveTemporal: string): Promise<Resultado> {
    return this.ejecutar(this.http.post<Usuario>(`/api/usuarios/${encodeURIComponent(id)}/clave`, { claveTemporal }));
  }

  desbloquear(id: string): Promise<Resultado> {
    return this.ejecutar(this.http.post<Usuario>(`/api/usuarios/${encodeURIComponent(id)}/desbloquear`, null));
  }

  private async ejecutar(peticion: Observable<Usuario>): Promise<Resultado> {
    try {
      return { ok: true, usuario: await firstValueFrom(peticion) };
    } catch (e) {
      const mensaje =
        e instanceof HttpErrorResponse && typeof e.error?.mensaje === 'string'
          ? (e.error.mensaje as string)
          : e instanceof HttpErrorResponse && e.status === 403
            ? 'Tu usuario ya no tiene permiso para administrar usuarios.'
            : 'No se pudo completar la acción. Intenta de nuevo.';
      return { ok: false, mensaje };
    }
  }
}
