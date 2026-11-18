import { Component, effect, ElementRef, inject, viewChild } from '@angular/core';
import { ConfirmacionService } from '../core/confirmacion';

/**
 * Diálogo modal de confirmación (F1.8b). Usa <dialog> con showModal(): el foco queda dentro, Escape cancela y el
 * lector de pantalla lo anuncia con su título. El foco empieza en "Cancelar" para que Enter no confirme por error.
 */
@Component({
  selector: 'app-dialogo-confirmacion',
  template: `
    <dialog
      #dialogo
      class="dialogo"
      aria-labelledby="dialogo-titulo"
      aria-describedby="dialogo-texto"
      (cancel)="$event.preventDefault(); servicio.responder(false)"
    >
      @if (servicio.pendiente(); as p) {
        <h2 id="dialogo-titulo">{{ p.titulo }}</h2>
        <p id="dialogo-texto">{{ p.texto }}</p>
        <div class="barra dialogo-acciones">
          <button type="button" #cancelar (click)="servicio.responder(false)">Cancelar</button>
          <button
            type="button"
            [class.principal]="!p.peligro"
            [class.peligro-solido]="p.peligro"
            (click)="servicio.responder(true)"
          >
            {{ p.aceptar }}
          </button>
        </div>
      }
    </dialog>
  `,
})
export class DialogoConfirmacion {
  protected readonly servicio = inject(ConfirmacionService);
  private readonly dialogo = viewChild.required<ElementRef<HTMLDialogElement>>('dialogo');
  private readonly cancelar = viewChild<ElementRef<HTMLButtonElement>>('cancelar');

  constructor() {
    effect(() => {
      const d = this.dialogo().nativeElement;
      if (this.servicio.pendiente()) {
        if (!d.open) {
          d.showModal();
        }
        queueMicrotask(() => this.cancelar()?.nativeElement.focus());
      } else if (d.open) {
        d.close();
      }
    });
  }
}
