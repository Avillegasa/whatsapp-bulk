import { DatePipe } from '@angular/common';
import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { utc } from '../campanias/campanias.service';
import { Baja, MonitorService } from './monitor.service';

/**
 * Bajas vigentes (F1.8, Operador). Dos capas separadas (regla 8): Empresa (pidió la baja a la empresa: BAJA, botón,
 * intranet, importación) y Plataforma (dejó de aceptar marketing en WhatsApp). Una no borra la otra.
 */
@Component({
  selector: 'app-bajas',
  imports: [DatePipe],
  template: `
    <h1>Bajas</h1>
    <p class="ayuda">
      Empresa: la persona pidió no recibir promociones (solo un nuevo «Sí» o un opt-in auditado la reactiva). Plataforma:
      la desactivó en WhatsApp ("Ofertas y anuncios"); un "resume" de WhatsApp nunca borra una baja de la empresa.
    </p>
    @if (error(); as e) {
      <p class="aviso error" role="status">{{ e }}</p>
    }
    @if (items(); as list) {
      <div class="resumen">
        <div><strong>{{ empresa() }}</strong>bajas de la empresa</div>
        <div><strong>{{ plataforma() }}</strong>bajas en WhatsApp</div>
      </div>
      @if (list.length === 0) {
        <p>No hay bajas vigentes.</p>
      } @else {
        <table>
          <thead>
            <tr><th>Contacto</th><th>Capa</th><th>Desde</th><th>Cómo</th></tr>
          </thead>
          <tbody>
            @for (b of list; track b.telefono + b.capa) {
              <tr>
                <td>{{ b.nombre ?? 'Sin nombre' }} <span class="sec">{{ b.telefono }}</span></td>
                <td>{{ b.capa }}{{ b.estado === 'NoContactar' ? ' (no contactar)' : '' }}</td>
                <td>{{ fecha(b.desdeUtc) | date: 'dd/MM/yyyy HH:mm' }}</td>
                <td>
                  {{ b.fuente }}
                  @if (b.evidencia) {
                    <br /><span class="sec">{{ b.evidencia }}</span>
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
      }
    } @else {
      <p>Cargando…</p>
    }
  `,
  styleUrls: ['../contactos/contactos.css'],
})
export class Bajas implements OnInit {
  private readonly api = inject(MonitorService);
  protected readonly items = signal<Baja[] | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly fecha = utc;
  protected readonly empresa = computed(() => (this.items() ?? []).filter((b) => b.capa === 'Empresa').length);
  protected readonly plataforma = computed(() => (this.items() ?? []).filter((b) => b.capa === 'Plataforma').length);

  async ngOnInit(): Promise<void> {
    try {
      this.items.set(await this.api.bajas());
    } catch {
      this.error.set('No se pudieron cargar las bajas.');
    }
  }
}
