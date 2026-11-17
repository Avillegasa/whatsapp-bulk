import { Component, computed, inject } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { MENU } from '../core/menu';

/** Marco de las pantallas con sesión: barra superior, menú según los roles del usuario y cierre de sesión. */
@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <header class="barra">
      <span class="marca">WhatsApp Bulk</span>
      <nav aria-label="Menú principal">
        @for (opcion of menu(); track opcion.ruta) {
          <a
            [routerLink]="opcion.ruta"
            routerLinkActive="activo"
            [routerLinkActiveOptions]="{ exact: opcion.ruta === '/' }"
            >{{ opcion.texto }}</a
          >
        }
      </nav>
      <span class="usuario">{{ auth.usuario() }} · {{ auth.roles().join(', ') }}</span>
      <button type="button" (click)="salir()">Cerrar sesión</button>
    </header>
    <main><router-outlet /></main>
  `,
  styles: `
    .barra { display: flex; align-items: center; gap: 1.5rem; padding: 0.75rem 1.5rem; background: #075e54; color: #fff; flex-wrap: wrap; }
    .marca { font-weight: 600; }
    nav { display: flex; gap: 1rem; flex: 1; }
    nav a { color: #d9fdd3; text-decoration: none; padding: 0.25rem 0; border-bottom: 2px solid transparent; }
    nav a.activo { color: #fff; border-bottom-color: #fff; }
    .usuario { font-size: 0.9rem; opacity: 0.9; }
    button { background: transparent; color: #fff; border: 1px solid #fff; border-radius: 4px; padding: 0.35rem 0.75rem; cursor: pointer; }
    main { padding: 1.5rem; max-width: 1100px; margin: 0 auto; }
  `,
})
export class Shell {
  protected readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  protected readonly menu = computed(() => MENU.filter((o) => this.auth.tieneAlgunRol(o.roles)));

  protected async salir(): Promise<void> {
    await this.auth.logout();
    await this.router.navigate(['/login']);
  }
}
