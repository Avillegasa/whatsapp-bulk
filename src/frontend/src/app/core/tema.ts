import { effect, Injectable, signal } from '@angular/core';

export type Tema = 'sistema' | 'claro' | 'oscuro';

const CLAVE = 'wabulk.tema';
const ORDEN: Tema[] = ['sistema', 'claro', 'oscuro'];
export const TEXTO_TEMA: Record<Tema, string> = {
  sistema: 'Sistema',
  claro: 'Claro',
  oscuro: 'Oscuro',
};

/**
 * Tema de la intranet (F1.8b): "sistema" sigue al sistema operativo; "claro" u "oscuro" lo fijan con el atributo
 * data-tema en <html> (los colores están en src/styles.css, sección Tema oscuro). La elección se guarda solo en este
 * navegador; si el almacenamiento no está disponible, queda "sistema".
 */
@Injectable({ providedIn: 'root' })
export class TemaService {
  readonly tema = signal<Tema>(leer());

  constructor() {
    effect(() => {
      const t = this.tema();
      const html = document.documentElement;
      if (t === 'sistema') {
        html.removeAttribute('data-tema');
      } else {
        html.setAttribute('data-tema', t);
      }
      try {
        localStorage.setItem(CLAVE, t);
      } catch {
        // Sin almacenamiento (modo privado, bloqueado): el tema dura solo esta pestaña.
      }
    });
  }

  siguiente(): void {
    this.tema.set(ORDEN[(ORDEN.indexOf(this.tema()) + 1) % ORDEN.length]);
  }
}

function leer(): Tema {
  try {
    const t = localStorage.getItem(CLAVE) as Tema | null;
    return t && ORDEN.includes(t) ? t : 'sistema';
  } catch {
    return 'sistema';
  }
}
