import { TestBed } from '@angular/core/testing';
import { ConfirmacionService } from '../core/confirmacion';
import { DialogoConfirmacion } from './dialogo-confirmacion';

describe('Diálogo de confirmación (F1.8b)', () => {
  beforeAll(() => {
    // jsdom no implementa showModal/close de <dialog>: lo mínimo para abrir y cerrar.
    const proto = HTMLDialogElement.prototype as unknown as Record<string, unknown>;
    proto['showModal'] ??= function (this: HTMLDialogElement) {
      this.setAttribute('open', '');
    };
    proto['close'] ??= function (this: HTMLDialogElement) {
      this.removeAttribute('open');
    };
  });

  async function abrir(peligro = false) {
    const fixture = TestBed.createComponent(DialogoConfirmacion);
    fixture.detectChanges();
    const servicio = TestBed.inject(ConfirmacionService);
    const respuesta = servicio.preguntar({ titulo: 'Desactivar usuario', texto: '¿Desactivar a ana?', aceptar: 'Desactivar', peligro });
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const boton = (texto: string) => Array.from(el.querySelectorAll('button')).find((b) => b.textContent?.trim() === texto)!;
    return { fixture, el, respuesta, boton };
  }

  it('muestra la pregunta y "Desactivar" responde sí', async () => {
    const { fixture, el, respuesta, boton } = await abrir(true);
    expect(el.querySelector('dialog')?.hasAttribute('open')).toBe(true);
    expect(el.textContent).toContain('¿Desactivar a ana?');
    expect(boton('Desactivar').classList).toContain('peligro-solido');

    boton('Desactivar').click();
    fixture.detectChanges();
    await expect(respuesta).resolves.toBe(true);
    expect(el.querySelector('dialog')?.hasAttribute('open')).toBe(false);
  });

  it('"Cancelar" responde no', async () => {
    const { fixture, respuesta, boton } = await abrir();
    boton('Cancelar').click();
    fixture.detectChanges();
    await expect(respuesta).resolves.toBe(false);
  });
});
