import { DatePipe } from '@angular/common';
import { Component, DestroyRef, inject, OnInit, signal } from '@angular/core';
import { Consentimiento, ContactosService, Conversacion, TEXTO_CONSENTIMIENTO } from '../contactos/contactos.service';
import { ICONOS } from '../../layout/iconos';
import { ConfirmacionService } from '../../core/confirmacion';
import { utc } from '../campanias/campanias.service';

/** Cada cuánto se recarga la lista (para ver llegar la respuesta del cliente sin tocar nada). */
export const RECARGA_MS = 15_000;

/**
 * Mensajes recientes (D-031): quién escribió en los últimos 7 días y el botón "Ofrecer promociones", que solo se
 * habilita con la ventana de 24 h abierta. La respuesta del cliente («Sí» / «Ahora no») actualiza el estado sola.
 */
@Component({
  selector: 'app-mensajes',
  imports: [DatePipe],
  templateUrl: './mensajes.html',
})
export class Mensajes implements OnInit {
  private readonly api = inject(ContactosService);
  private readonly confirmacion = inject(ConfirmacionService);
  protected readonly conversaciones = signal<Conversacion[] | null>(null);
  protected readonly errorCarga = signal<string | null>(null);
  protected readonly aviso = signal<{ tipo: 'ok' | 'error'; texto: string } | null>(null);
  protected readonly ocupado = signal<number | null>(null);
  protected readonly iconos = ICONOS;
  /** La Api manda las fechas UTC sin "Z": utc() las toma como UTC y el pipe las muestra en la hora local. */
  protected readonly fecha = utc;

  constructor() {
    const id = setInterval(() => void this.cargar(), RECARGA_MS);
    inject(DestroyRef).onDestroy(() => clearInterval(id));
  }

  async ngOnInit(): Promise<void> {
    await this.cargar();
  }

  protected texto(c: Consentimiento): string {
    return TEXTO_CONSENTIMIENTO[c];
  }

  protected async ofrecer(c: Conversacion): Promise<void> {
    const quien = c.nombre ?? c.telefono ?? `contacto ${c.contactoId}`;
    const si = await this.confirmacion.preguntar({
      titulo: 'Ofrecer promociones',
      texto: `¿Enviar a ${quien} la pregunta para recibir promociones?`,
      aceptar: 'Enviar pregunta',
    });
    if (!si) {
      return;
    }

    this.ocupado.set(c.contactoId);
    this.aviso.set(null);
    const r = await this.api.ofrecer(c.contactoId);
    this.ocupado.set(null);
    this.aviso.set(
      r.ok
        ? { tipo: r.valor.enviada ? 'ok' : 'error', texto: r.valor.mensaje }
        : { tipo: 'error', texto: r.mensaje },
    );
    await this.cargar();
  }

  protected async cargar(): Promise<void> {
    try {
      this.conversaciones.set(await this.api.conversaciones());
      this.errorCarga.set(null);
    } catch {
      this.errorCarga.set('No se pudieron cargar los mensajes.');
    }
  }
}
