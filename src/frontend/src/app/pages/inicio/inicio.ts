import { Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth.service';
import { MENU } from '../../core/menu';
import { ICONOS } from '../../layout/iconos';

/** Qué se hace en cada pantalla (texto corto de los accesos directos de Inicio, F1.8b). */
const DESCRIPCION: Record<string, string> = {
  '/mensajes': 'Quién escribió y a quién ofrecer promociones',
  '/contactos': 'Buscar clientes, ver su consentimiento y registrar bajas',
  '/importar': 'Cargar contactos desde .csv o .xlsx',
  '/listas': 'Grupos de contactos para las campañas',
  '/plantillas': 'Mensajes aprobados por Meta',
  '/campanias': 'Preparar envíos y mandarlos a aprobación',
  '/monitor': 'Despacho en vivo, capacidad y pausas',
  '/revision': 'Envíos que Meta no confirmó',
  '/bajas': 'Quién pidió no recibir promociones',
  '/aprobaciones': 'Campañas que esperan tu visto bueno',
  '/alertas': 'Freno de emergencia y reanudar',
  '/usuarios': 'Cuentas, roles y claves',
};

/** Inicio: saludo y accesos directos a las pantallas que permite el rol del usuario. */
@Component({
  selector: 'app-inicio',
  imports: [RouterLink],
  template: `
    <h1>Hola, {{ auth.usuario() }}</h1>
    <p class="ayuda">Tus roles: {{ auth.roles().join(', ') }}.</p>
    <div class="accesos">
      @for (o of accesos(); track o.ruta) {
        <a class="acceso" [routerLink]="o.ruta">
          <svg class="icono" viewBox="0 0 24 24" aria-hidden="true">
            <path [attr.d]="iconos[o.ruta]" />
          </svg>
          <strong>{{ o.texto }}</strong>
          <span>{{ descripcion[o.ruta] }}</span>
        </a>
      }
    </div>
  `,
})
export class Inicio {
  protected readonly auth = inject(AuthService);
  protected readonly iconos = ICONOS;
  protected readonly descripcion = DESCRIPCION;
  protected readonly accesos = computed(() =>
    MENU.filter((o) => o.ruta !== '/' && this.auth.tieneAlgunRol(o.roles)),
  );
}
