/**
 * Lleva el foco y la vista al detalle que se acaba de abrir (F1.8b): quien usa teclado o lector de pantalla llega
 * directo a lo que pidió en lugar de quedarse en la tabla. El detalle necesita tabindex="-1".
 */
export function enfocarDetalle(selector = '.detalle'): void {
  setTimeout(() => {
    const el = document.querySelector<HTMLElement>(selector);
    el?.focus({ preventScroll: true });
    el?.scrollIntoView?.({ behavior: 'smooth', block: 'start' });
  });
}
