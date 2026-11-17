import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { computed, inject, Injectable, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export type Rol = 'Operador' | 'Aprobador' | 'Admin';

export interface Sesion {
  autenticado: boolean;
  usuario: string | null;
  roles: Rol[];
}

export interface LoginError {
  error: 'Credenciales' | 'Bloqueado' | string;
  mensaje: string;
}

const SIN_SESION: Sesion = { autenticado: false, usuario: null, roles: [] };

/**
 * Estado de la sesión (cookie HttpOnly de la Api: Angular nunca ve la cookie de sesión, solo pregunta /api/auth/sesion).
 * Cada respuesta de /api/auth/* renueva la cookie XSRF-TOKEN, que HttpClient copia al encabezado X-XSRF-TOKEN
 * en los POST/PUT/PATCH/DELETE a URLs relativas (https://angular.dev/best-practices/security#httpclient-xsrf-csrf-security).
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly sesion = signal<Sesion>(SIN_SESION);

  readonly autenticado = computed(() => this.sesion().autenticado);
  readonly usuario = computed(() => this.sesion().usuario);
  readonly roles = computed(() => this.sesion().roles);

  tieneAlgunRol(roles: readonly Rol[]): boolean {
    return roles.length === 0 || roles.some((r) => this.roles().includes(r));
  }

  /** Al arrancar la app: trae la sesión (si la cookie sigue vigente) y la cookie XSRF para poder hacer el login. */
  async cargarSesion(): Promise<void> {
    try {
      this.sesion.set(await firstValueFrom(this.http.get<Sesion>('/api/auth/sesion')));
    } catch {
      this.sesion.set(SIN_SESION);
    }
  }

  /** Devuelve null si entró, o el error de la Api (credenciales o bloqueo). */
  async login(usuario: string, clave: string): Promise<LoginError | null> {
    try {
      this.sesion.set(await firstValueFrom(this.http.post<Sesion>('/api/auth/login', { usuario, clave })));
      return null;
    } catch (e) {
      if (e instanceof HttpErrorResponse && e.status === 401 && e.error?.error) {
        return e.error as LoginError;
      }
      if (e instanceof HttpErrorResponse && e.status === 400) {
        // Token XSRF vencido o ausente: se pide uno nuevo para el siguiente intento.
        await this.cargarSesion();
        return { error: 'Xsrf', mensaje: 'La sesión del formulario venció. Intenta de nuevo.' };
      }
      return { error: 'Red', mensaje: 'No se pudo conectar con el servidor.' };
    }
  }

  async logout(): Promise<void> {
    try {
      this.sesion.set(await firstValueFrom(this.http.post<Sesion>('/api/auth/logout', null)));
    } catch {
      this.sesion.set(SIN_SESION);
    }
  }

  /** La Api respondió 401 en otra llamada: la cookie venció o se cerró la sesión en otro lado. */
  sesionPerdida(): void {
    this.sesion.set(SIN_SESION);
  }
}
