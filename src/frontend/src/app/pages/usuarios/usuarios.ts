import { DatePipe } from '@angular/common';
import { Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AuthService, Rol } from '../../core/auth.service';
import { ConfirmacionService } from '../../core/confirmacion';
import { Resultado, Usuario, UsuariosService } from './usuarios.service';

const ROLES: Rol[] = ['Operador', 'Aprobador', 'Admin'];

/** Pantalla de usuarios (solo Admin): crear, roles, activar/desactivar, restablecer clave y desbloquear. */
@Component({
  selector: 'app-usuarios',
  imports: [DatePipe, FormsModule],
  templateUrl: './usuarios.html',
})
export class Usuarios implements OnInit {
  private readonly api = inject(UsuariosService);
  private readonly confirmacion = inject(ConfirmacionService);
  protected readonly auth = inject(AuthService);
  protected readonly roles = ROLES;

  protected readonly usuarios = signal<Usuario[] | null>(null);
  protected readonly errorCarga = signal<string | null>(null);
  protected readonly aviso = signal<{ tipo: 'ok' | 'error'; texto: string } | null>(null);
  protected readonly ocupado = signal(false);

  /** Roles marcados en cada fila antes de guardar (id → roles). */
  protected readonly rolesEditados = signal<Record<string, Rol[]>>({});
  /** Fila con el formulario de clave abierto. */
  protected readonly claveAbierta = signal<string | null>(null);
  protected claveNueva = '';

  protected nuevo = { usuario: '', roles: [] as Rol[], clave: '' };

  async ngOnInit(): Promise<void> {
    await this.cargar();
  }

  protected rolesDe(u: Usuario): Rol[] {
    return this.rolesEditados()[u.id] ?? u.roles;
  }

  protected rolesCambiados(u: Usuario): boolean {
    const editados = this.rolesEditados()[u.id];
    return !!editados && [...editados].sort().join() !== [...u.roles].sort().join();
  }

  protected alternarRol(u: Usuario, rol: Rol): void {
    const actuales = this.rolesDe(u);
    const nuevos = actuales.includes(rol) ? actuales.filter((r) => r !== rol) : [...actuales, rol];
    this.rolesEditados.update((m) => ({ ...m, [u.id]: nuevos }));
  }

  protected alternarRolNuevo(rol: Rol): void {
    const r = this.nuevo.roles;
    this.nuevo.roles = r.includes(rol) ? r.filter((x) => x !== rol) : [...r, rol];
  }

  protected bloqueado(u: Usuario): boolean {
    return u.bloqueadoHasta !== null && new Date(u.bloqueadoHasta).getTime() > Date.now();
  }

  protected esYo(u: Usuario): boolean {
    return u.usuario === this.auth.usuario();
  }

  protected async crear(): Promise<void> {
    const { usuario, roles, clave } = this.nuevo;
    // El formulario se vacía apenas la Api confirma (antes de recargar la lista), para que no quede a la vista el
    // usuario anterior con sus roles marcados mientras llega la lista nueva (F1.8b, lo encontró la prueba E2E).
    await this.accion(() => this.api.crear(usuario.trim(), roles, clave), `Usuario ${usuario.trim()} creado.`, () => {
      this.nuevo = { usuario: '', roles: [], clave: '' };
    });
  }

  protected async guardarRoles(u: Usuario): Promise<void> {
    await this.accion(() => this.api.cambiarRoles(u.id, this.rolesDe(u)), `Roles de ${u.usuario} actualizados.`);
  }

  protected async cambiarActivo(u: Usuario): Promise<void> {
    if (
      u.activo &&
      !(await this.confirmacion.preguntar({
        titulo: 'Desactivar usuario',
        texto: `¿Desactivar a ${u.usuario}? No podrá entrar y su sesión abierta se cierra.`,
        aceptar: 'Desactivar',
        peligro: true,
      }))
    ) {
      return;
    }
    await this.accion(
      () => this.api.activar(u.id, !u.activo),
      u.activo ? `${u.usuario} desactivado.` : `${u.usuario} activado.`,
    );
  }

  protected async restablecer(u: Usuario): Promise<void> {
    const ok = await this.accion(
      () => this.api.restablecerClave(u.id, this.claveNueva),
      `Clave de ${u.usuario} restablecida. Entrégasela en persona; sus sesiones abiertas se cerraron.`,
    );
    if (ok) {
      this.claveAbierta.set(null);
      this.claveNueva = '';
    }
  }

  protected async desbloquear(u: Usuario): Promise<void> {
    await this.accion(() => this.api.desbloquear(u.id), `${u.usuario} desbloqueado.`);
  }

  protected abrirClave(u: Usuario): void {
    this.claveNueva = '';
    this.claveAbierta.set(this.claveAbierta() === u.id ? null : u.id);
  }

  private async cargar(): Promise<void> {
    try {
      this.usuarios.set(await this.api.listar());
      this.rolesEditados.set({});
      this.errorCarga.set(null);
    } catch {
      this.errorCarga.set('No se pudo cargar la lista de usuarios.');
    }
  }

  /** Ejecuta una acción, muestra el resultado y recarga la lista. Devuelve true si salió bien. */
  private async accion(hacer: () => Promise<Resultado>, exito: string, alConfirmar?: () => void): Promise<boolean> {
    this.ocupado.set(true);
    this.aviso.set(null);
    const r = await hacer();
    this.ocupado.set(false);
    if (!r.ok) {
      this.aviso.set({ tipo: 'error', texto: r.mensaje });
      return false;
    }
    this.aviso.set({ tipo: 'ok', texto: exito });
    alConfirmar?.();
    await this.cargar();
    return true;
  }
}
