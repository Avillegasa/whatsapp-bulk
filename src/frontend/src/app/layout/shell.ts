import { Component, computed, HostListener, inject, signal } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { MENU, OpcionMenu } from '../core/menu';
import { TEXTO_TEMA, TemaService } from '../core/tema';
import { ICONOS } from './iconos';

interface GrupoMenu {
  titulo: string | null;
  opciones: OpcionMenu[];
}

/**
 * Marco de las pantallas con sesión (F1.8b): menú lateral agrupado según los roles del usuario, cabecera con el
 * usuario y "Cerrar sesión". En pantallas angostas el menú se abre con el botón "Menú" y se cierra al elegir una
 * opción, al tocar fuera o con Escape. Los estilos están en src/styles.css (sección Layout).
 */
@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <a class="saltar" href="#principal" (click)="irAlContenido($event)">Saltar al contenido</a>
    <div class="marco">
      <aside class="lateral" id="menu-lateral" [class.abierto]="menuAbierto()">
        <a class="marca" routerLink="/" (click)="menuAbierto.set(false)">
          <span class="marca-logo" aria-hidden="true">
            <svg class="icono" viewBox="0 0 24 24"><path [attr.d]="iconos['marca']" /></svg>
          </span>
          <span class="marca-texto">
            <strong>WhatsApp Bulk</strong>
            <span>Promociones a clientes</span>
          </span>
        </a>
        <nav aria-label="Menú principal">
          @for (grupo of grupos(); track $index) {
            @if (grupo.titulo) {
              <p class="grupo-menu">{{ grupo.titulo }}</p>
            }
            @for (opcion of grupo.opciones; track opcion.ruta) {
              <a
                [routerLink]="opcion.ruta"
                routerLinkActive="activo"
                ariaCurrentWhenActive="page"
                [routerLinkActiveOptions]="{ exact: opcion.ruta === '/' }"
                (click)="menuAbierto.set(false)"
                ><svg class="icono" viewBox="0 0 24 24" aria-hidden="true">
                  <path [attr.d]="icono(opcion.ruta)" /></svg
                ><span>{{ opcion.texto }}</span></a
              >
            }
          }
        </nav>
      </aside>
      <div
        class="velo"
        [class.visible]="menuAbierto()"
        (click)="menuAbierto.set(false)"
        aria-hidden="true"
      ></div>

      <div class="contenido">
        <header class="cabecera">
          <button
            type="button"
            class="boton-menu fantasma"
            aria-controls="menu-lateral"
            [attr.aria-expanded]="menuAbierto()"
            (click)="menuAbierto.set(!menuAbierto())"
          >
            <svg class="icono" viewBox="0 0 24 24" aria-hidden="true">
              <path [attr.d]="menuAbierto() ? iconos['cerrar'] : iconos['menu']" />
            </svg>
            Menú
          </button>
          <span class="espacio"></span>
          <button
            type="button"
            class="chico fantasma boton-tema"
            [attr.aria-label]="'Tema: ' + textoTema[temas.tema()] + '. Cambiar'"
            [title]="'Tema: ' + textoTema[temas.tema()]"
            (click)="temas.siguiente()"
          >
            <svg class="icono" viewBox="0 0 24 24" aria-hidden="true"><path [attr.d]="iconos[temas.tema()]" /></svg>
            <span class="texto-salir">{{ textoTema[temas.tema()] }}</span>
          </button>
          <div class="usuario">
            <span class="avatar" aria-hidden="true">{{ inicial() }}</span>
            <span class="usuario-datos">
              <strong>{{ auth.usuario() }}</strong>
              <span>{{ auth.roles().join(', ') }}</span>
            </span>
          </div>
          <button
            type="button"
            class="chico boton-salir"
            aria-label="Cerrar sesión"
            (click)="salir()"
          >
            <svg class="icono" viewBox="0 0 24 24" aria-hidden="true">
              <path [attr.d]="iconos['salir']" />
            </svg>
            <span class="texto-salir">Cerrar sesión</span>
          </button>
        </header>
        <main class="pagina" id="principal" tabindex="-1"><router-outlet /></main>
      </div>
    </div>
  `,
})
export class Shell {
  protected readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  protected readonly iconos = ICONOS;
  protected readonly menuAbierto = signal(false);
  protected readonly temas = inject(TemaService);
  protected readonly textoTema = TEXTO_TEMA;
  protected readonly menu = computed(() => MENU.filter((o) => this.auth.tieneAlgunRol(o.roles)));

  /** Opciones seguidas con el mismo grupo forman una sección del menú (el orden de MENU se respeta). */
  protected readonly grupos = computed(() => {
    const grupos: GrupoMenu[] = [];
    for (const opcion of this.menu()) {
      const titulo = opcion.grupo ?? null;
      const ultimo = grupos.at(-1);
      if (ultimo && ultimo.titulo === titulo) {
        ultimo.opciones.push(opcion);
      } else {
        grupos.push({ titulo, opciones: [opcion] });
      }
    }
    return grupos;
  });

  protected readonly inicial = computed(() => (this.auth.usuario() ?? '?').charAt(0));

  protected icono(ruta: string): string {
    return ICONOS[ruta] ?? ICONOS['/'];
  }

  /** "Saltar al contenido": lleva el foco del teclado al inicio de la pantalla, sin pasar por el menú. */
  protected irAlContenido(e: Event): void {
    e.preventDefault();
    document.getElementById('principal')?.focus();
  }

  @HostListener('document:keydown.escape')
  protected cerrarMenu(): void {
    this.menuAbierto.set(false);
  }

  protected async salir(): Promise<void> {
    await this.auth.logout();
    await this.router.navigate(['/login']);
  }
}
