import { DatePipe } from '@angular/common';
import { Component, inject, OnInit, signal } from '@angular/core';
import { utc } from '../campanias/campanias.service';
import { Alerta, MonitorService, Pausa } from './monitor.service';

/** Qué revisar antes de reanudar, por tipo de alerta. */
const QUE_REVISAR: Record<string, string> = {
  Token: 'El token de Meta venció o es inválido: rotarlo en user-secrets (Meta:AccessToken) y reiniciar Api y Worker.',
  Facturacion: 'Problema con el método de pago de la cuenta de Meta: revisarlo en el Administrador de WhatsApp.',
  PlantillaPausada: 'Meta pausó una plantilla por baja calidad: revisar el contenido o usar otra plantilla.',
  Calidad: 'Meta limitó los envíos del número (131048): revisar la calidad en el Administrador de WhatsApp.',
  Politica: 'La cuenta fue restringida por una política (131031/368): revisar la cuenta en el Administrador de WhatsApp.',
  TasaFallos: 'Más del 5 % de los últimos envíos falló: revisar los errores en el monitor antes de seguir.',
};

/**
 * Alertas y freno de emergencia (F1.8, Aprobador). "Reanudar" levanta la pausa global y resuelve las alertas; queda en
 * AuditLog con el usuario de la intranet. Si el problema sigue, el próximo envío vuelve a pausar.
 */
@Component({
  selector: 'app-alertas',
  imports: [DatePipe],
  template: `
    <h1>Alertas</h1>
    @if (aviso(); as a) {
      <p class="aviso" [class.error]="a.tipo === 'error'" role="status">{{ a.texto }}</p>
    }
    @if (pausa(); as p) {
      @if (p.enPausa) {
        <p class="aviso error" role="alert">
          Despacho EN PAUSA por {{ p.motivo }} desde {{ fecha(p.desdeUtc) | date: 'dd/MM HH:mm' }}.
        </p>
      } @else {
        <p class="aviso" role="status">Despacho activo.</p>
      }
    }
    @if (alertas(); as list) {
      @if (list.length === 0) {
        <p>No hay alertas abiertas.</p>
      } @else {
        <table class="apilable">
          <thead>
            <tr><th>Alerta</th><th>Veces</th><th>Última</th></tr>
          </thead>
          <tbody>
            @for (a of list; track a.id) {
              <tr>
                <td data-label="Alerta">
                  <strong>{{ a.tipo }}</strong> {{ a.mensaje }} <span class="sec">({{ a.origen }})</span>
                  <br /><span class="sec">{{ queRevisar(a.tipo) }}</span>
                </td>
                <td data-label="Veces">{{ a.ocurrencias }}</td>
                <td data-label="Última">{{ fecha(a.ultimaUtc) | date: 'dd/MM HH:mm' }}</td>
              </tr>
            }
          </tbody>
        </table>
      }
      @if (pausa()?.enPausa || list.length > 0) {
        <div class="barra">
          <button type="button" class="principal" [disabled]="ocupado()" (click)="reanudar()">
            Revisé el problema: reanudar el despacho
          </button>
        </div>
      }
    } @else {
      <p>Cargando…</p>
    }
  `,
})
export class Alertas implements OnInit {
  private readonly api = inject(MonitorService);
  protected readonly pausa = signal<Pausa | null>(null);
  protected readonly alertas = signal<Alerta[] | null>(null);
  protected readonly aviso = signal<{ tipo: 'ok' | 'error'; texto: string } | null>(null);
  protected readonly ocupado = signal(false);
  protected readonly fecha = utc;

  async ngOnInit(): Promise<void> {
    await this.cargar();
  }

  protected queRevisar(tipo: string): string {
    return QUE_REVISAR[tipo] ?? '';
  }

  protected async reanudar(): Promise<void> {
    this.ocupado.set(true);
    const r = await this.api.reanudar();
    this.ocupado.set(false);
    this.aviso.set(r.ok ? { tipo: 'ok', texto: r.valor.mensaje } : { tipo: 'error', texto: r.mensaje });
    await this.cargar();
  }

  private async cargar(): Promise<void> {
    try {
      const r = await this.api.alertas();
      this.pausa.set(r.pausa);
      this.alertas.set(r.abiertas);
    } catch {
      this.aviso.set({ tipo: 'error', texto: 'No se pudieron cargar las alertas.' });
    }
  }
}
