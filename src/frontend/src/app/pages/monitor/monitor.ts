import { DatePipe, KeyValuePipe } from '@angular/common';
import { Component, computed, inject, OnDestroy, OnInit, signal } from '@angular/core';
import { TEXTO_ESTADO_CAMPANA, EstadoCampana, utc } from '../campanias/campanias.service';
import { MonitorService, Tablero, TEXTO_OMISION } from './monitor.service';
import { ICONOS } from '../../layout/iconos';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth.service';

/**
 * Monitor en vivo (F1.8, Operador): estado del despacho, capacidad del día y contadores por campaña. La Api empuja los
 * cambios por SignalR (menos de 5 s); "Actualizar" pide el tablero de nuevo si la conexión se cortó.
 */
@Component({
  selector: 'app-monitor',
  imports: [DatePipe, KeyValuePipe, RouterLink],
  templateUrl: './monitor.html',
})
export class Monitor implements OnInit, OnDestroy {
  private readonly api = inject(MonitorService);
  protected readonly tablero = signal<Tablero | null>(null);
  protected readonly enVivo = signal(false);
  protected readonly actualizadoUtc = signal<Date | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly fecha = utc;
  protected readonly auth = inject(AuthService);
  protected readonly iconos = ICONOS;
  private cerrar: (() => Promise<void>) | null = null;

  /** Suma de todas las campañas del tablero, para los contadores grandes (F1.8b). */
  protected readonly totales = computed(() => {
    const t = { enCola: 0, enviados: 0, entregados: 0, leidos: 0, fallidos: 0, omitidos: 0 };
    for (const c of this.tablero()?.campanias ?? []) {
      t.enCola += c.enCola + c.enviandose;
      t.enviados += c.enviados;
      t.entregados += c.entregados;
      t.leidos += c.leidos;
      t.fallidos += c.fallidos;
      t.omitidos += Object.values(c.omitidos).reduce((a, b) => a + b, 0);
    }
    return t;
  });

  /** Porcentaje del presupuesto de capacidad ya usado (barra del tablero). */
  protected readonly usoCapacidad = computed(() => {
    const c = this.tablero()?.capacidad;
    return c && c.presupuesto > 0 ? Math.min(100, Math.round((c.usados * 100) / c.presupuesto)) : 0;
  });

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
