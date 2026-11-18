import { Component, computed, input, model } from '@angular/core';

/**
 * Paginador de tablas largas (F1.8b): "1–25 de 120" con Anterior y Siguiente. Solo divide lo que ya llegó de la Api;
 * no hace llamadas. La pantalla toma la página con `pagina` y corta su lista.
 */
@Component({
  selector: 'app-paginador',
  template: `
    @if (total() > tamano()) {
      <nav class="paginador" [attr.aria-label]="'Páginas de ' + que()">
        <span class="sec" aria-live="polite">{{ desde() }}–{{ hasta() }} de {{ total() }}</span>
        <button
          type="button"
          class="chico"
          [disabled]="pagina() === 0"
          (click)="pagina.set(pagina() - 1)"
        >
          Anterior
        </button>
        <button
          type="button"
          class="chico"
          [disabled]="hasta() >= total()"
          (click)="pagina.set(pagina() + 1)"
        >
          Siguiente
        </button>
      </nav>
    }
  `,
})
export class Paginador {
  readonly total = input.required<number>();
  readonly tamano = input(25);
  readonly que = input('la tabla');
  readonly pagina = model(0);
  protected readonly desde = computed(() => this.pagina() * this.tamano() + 1);
  protected readonly hasta = computed(() =>
    Math.min(this.total(), (this.pagina() + 1) * this.tamano()),
  );
}

/** La parte de la lista que corresponde a una página. */
export function paginaDe<T>(lista: readonly T[], pagina: number, tamano: number): T[] {
  return lista.slice(pagina * tamano, (pagina + 1) * tamano);
}
