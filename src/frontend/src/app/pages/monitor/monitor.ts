import { DatePipe, KeyValuePipe } from '@angular/common';
import { Component, inject, OnDestroy, OnInit, signal } from '@angular/core';
import { TEXTO_ESTADO_CAMPANA, EstadoCampana, utc } from '../campanias/campanias.service';
import { MonitorService, Tablero, TEXTO_OMISION } from './monitor.service';

/**
 * Monitor en vivo (F1.8, Operador): estado del despacho, capacidad del día y contadores por campaña. La Api empuja los
 * cambios por SignalR (menos de 5 s); "Actualizar" pide el tablero de nuevo si la conexión se cortó.
 */
@Component({
  selector: 'app-monitor',
  imports: [DatePipe, KeyValuePipe],
  templateUrl: './monitor.html',
})
export class Monitor implements OnInit, OnDestroy {
  private readonly api = inject(MonitorService);
  protected readonly tablero = signal<Tablero | null>(null);
  protected readonly enVivo = signal(false);
  protected readonly actualizadoUtc = signal<Date | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly fecha = utc;
  private cerrar: (() => Promise<void>) | null = null;

  async ngOnInit(): Promise<void> {
    await this.actualizar();
    this.cerrar = await this.api.conectar(
      (t) => this.recibir(t),
      (c) => this.enVivo.set(c),
    );
  }

  async ngOnDestroy(): Promise<void> {
    await this.cerrar?.();
  }

  protected estado(e: string): string {
    return TEXTO_ESTADO_CAMPANA[e as EstadoCampana] ?? e;
  }

  protected omision(m: string): string {
    return TEXTO_OMISION[m] ?? m;
  }

  protected porcentaje(parte: number, total: number): number {
    return total > 0 ? Math.round((parte * 100) / total) : 0;
  }

  protected async actualizar(): Promise<void> {
    try {
      this.recibir(await this.api.tablero());
      this.error.set(null);
    } catch {
      this.error.set('No se pudo cargar el tablero.');
    }
  }

  private recibir(t: Tablero): void {
    this.tablero.set(t);
    this.actualizadoUtc.set(new Date());
  }
}
