import { DatePipe } from '@angular/common';
import { Component, inject, OnInit, signal } from '@angular/core';
import { utc } from '../campanias/campanias.service';
import { Incierto, MonitorService } from './monitor.service';

/**
 * Envíos inciertos (F1.8, Operador): Meta no respondió con certeza y ningún webhook lo aclaró. Una persona confirma si
 * llegó (por ejemplo, preguntando al cliente). Nunca se reenvía solo (regla 4).
 */
@Component({
  selector: 'app-revision',
  imports: [DatePipe],
  template: `
    <h1>Envíos inciertos</h1>
    @if (aviso(); as a) {
      <p class="aviso" [class.error]="a.tipo === 'error'" role="status">{{ a.texto }}</p>
    }
    <p class="ayuda">
      Meta no confirmó si estos mensajes salieron. Si llega un webhook se resuelven solos; los
      marcados "vencido" ya esperaron el plazo. Marca "Llegó" o "No llegó": el sistema nunca los
      reenvía.
    </p>
    @if (items(); as list) {
      @if (list.length === 0) {
        <p class="vacio">No hay envíos inciertos.</p>
      } @else {
        <div class="tabla">
          <table class="apilable">
            <thead>
              <tr>
                <th>Contacto</th>
                <th>Campaña</th>
                <th>Enviado</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              @for (i of list; track i.trabajoId) {
                <tr>
                  <td data-label="Contacto" class="principal-celda">
                    {{ i.nombre ?? 'Sin nombre' }} <span class="sec">{{ i.telefono }}</span>
                  </td>
                  <td data-label="Campaña">{{ i.campana ?? 'envío de prueba' }}</td>
                  <td data-label="Enviado">
                    {{ fecha(i.inicioUtc) | date: 'dd/MM HH:mm' }}
                    @if (i.vencido) {
                      <br /><span class="estado PendingApproval">vencido</span>
                    }
                  </td>
                  <td data-label="">
                    <button
                      type="button"
                      class="chico"
                      [disabled]="ocupado()"
                      (click)="marcar(i, true)"
                    >
                      Llegó
                    </button>
                    <button
                      type="button"
                      class="chico"
                      [disabled]="ocupado()"
                      (click)="marcar(i, false)"
                      class="chico peligro"
                    >
                      No llegó
                    </button>
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    } @else {
      <p class="cargando">Cargando…</p>
    }
  `,
})
export class Revision implements OnInit {
  private readonly api = inject(MonitorService);
  protected readonly items = signal<Incierto[] | null>(null);
  protected readonly aviso = signal<{ tipo: 'ok' | 'error'; texto: string } | null>(null);
  protected readonly ocupado = signal(false);
  protected readonly fecha = utc;

  async ngOnInit(): Promise<void> {
    await this.cargar();
  }

  protected async marcar(i: Incierto, llego: boolean): Promise<void> {
    this.ocupado.set(true);
    const r = await this.api.marcar(i.trabajoId, llego);
    this.ocupado.set(false);
    this.aviso.set(
      r.ok ? { tipo: 'ok', texto: r.valor.mensaje } : { tipo: 'error', texto: r.mensaje },
    );
    await this.cargar();
  }

  private async cargar(): Promise<void> {
    try {
      this.items.set(await this.api.inciertos());
    } catch {
      this.aviso.set({ tipo: 'error', texto: 'No se pudieron cargar los envíos inciertos.' });
    }
  }
}
