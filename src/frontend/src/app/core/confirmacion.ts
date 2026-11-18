import { Injectable, signal } from '@angular/core';

export interface Pregunta {
  titulo: string;
  texto: string;
  /** Texto del botón que confirma (por ejemplo, "Enviar pregunta"). */
  aceptar: string;
  /** Acción que quita algo (desactivar, cancelar): el botón va en rojo. */
  peligro?: boolean;
}

interface Pendiente extends Pregunta {
  responder: (si: boolean) => void;
}

/**
 * Confirmaciones con el diálogo propio de la intranet (F1.8b) en lugar de confirm() del navegador. El diálogo lo dibuja
 * el marco (DialogoConfirmacion, dentro de Shell); una pantalla solo pide `await preguntar(...)`.
 */
@Injectable({ providedIn: 'root' })
export class ConfirmacionService {
  readonly pendiente = signal<Pendiente | null>(null);

  preguntar(p: Pregunta): Promise<boolean> {
    this.pendiente()?.responder(false);
    return new Promise((resolve) => this.pendiente.set({ ...p, responder: resolve }));
  }

  responder(si: boolean): void {
    const p = this.pendiente();
    this.pendiente.set(null);
    p?.responder(si);
  }
}
