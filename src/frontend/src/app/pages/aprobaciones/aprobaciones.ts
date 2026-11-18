import { DatePipe } from '@angular/common';
import { Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Campana, CampanaDetalle, CampaniasService, utc, vistaPrevia } from '../campanias/campanias.service';

/**
 * Aprobaciones (F1.6, Aprobador): campañas por aprobar. Se ve exactamente la versión que se aprueba (mensaje con los
 * parámetros, lista, cantidad de destinatarios, fechas y hash). Quien creó o editó la campaña recibe 403 al aprobar.
 */
@Component({
  selector: 'app-aprobaciones',
  imports: [FormsModule, DatePipe],
  templateUrl: './aprobaciones.html',
})
export class Aprobaciones implements OnInit {
  private readonly api = inject(CampaniasService);
  protected readonly pendientes = signal<Campana[] | null>(null);
  protected readonly abierta = signal<CampanaDetalle | null>(null);
  protected readonly aviso = signal<{ tipo: 'ok' | 'error'; texto: string } | null>(null);
  protected readonly ocupado = signal(false);
  protected readonly fecha = utc;
  protected motivo = '';

  async ngOnInit(): Promise<void> {
    await this.cargar();
  }

  protected async ver(c: Campana): Promise<void> {
    this.motivo = '';
    this.abierta.set(await this.api.detallePendiente(c.id));
  }

  protected previa(d: CampanaDetalle): string {
    return vistaPrevia(d.cuerpo, d.versiones[0]?.parametros ?? []);
  }

  protected async aprobar(d: CampanaDetalle): Promise<void> {
    const v = d.versiones[0];
    this.ocupado.set(true);
    const r = await this.api.aprobar(d.campana.id, v.id, v.hash);
    await this.terminar(r.ok ? { tipo: 'ok', texto: r.valor.mensaje } : { tipo: 'error', texto: r.mensaje }, r.ok);
  }

  protected async rechazar(d: CampanaDetalle): Promise<void> {
    this.ocupado.set(true);
    const r = await this.api.rechazar(d.campana.id, d.versiones[0].id, this.motivo.trim());
    await this.terminar(r.ok ? { tipo: 'ok', texto: r.valor.mensaje } : { tipo: 'error', texto: r.mensaje }, r.ok);
  }

  protected async cargar(): Promise<void> {
    try {
      this.pendientes.set(await this.api.pendientes());
    } catch {
      this.aviso.set({ tipo: 'error', texto: 'No se pudieron cargar las campañas por aprobar.' });
    }
  }

  private async terminar(aviso: { tipo: 'ok' | 'error'; texto: string }, cerrar: boolean): Promise<void> {
    this.ocupado.set(false);
    this.aviso.set(aviso);
    if (cerrar) this.abierta.set(null);
    await this.cargar();
  }
}
