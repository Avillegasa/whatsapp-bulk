import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ContactosService, Importacion, Lista } from '../contactos/contactos.service';

/** Importación de contactos desde CSV o Excel (F1.4) con reporte por fila. */
@Component({
  selector: 'app-importar',
  imports: [FormsModule],
  templateUrl: './importar.html',
})
export class Importar implements OnInit {
  private readonly api = inject(ContactosService);
  protected readonly listas = signal<Lista[]>([]);
  protected readonly archivo = signal<File | null>(null);
  protected readonly reporte = signal<Importacion | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly ocupado = signal(false);
  protected readonly soloProblemas = signal(true);
  protected listaId: number | null = null;

  protected readonly filas = computed(() => {
    const r = this.reporte();
    if (!r) return [];
    return this.soloProblemas() ? r.detalle.filter((d) => d.result === 'Error' || d.result === 'Advertencia') : r.detalle;
  });

  async ngOnInit(): Promise<void> {
    try {
      this.listas.set(await this.api.listas());
    } catch {
      this.listas.set([]);
    }
  }

  protected elegir(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.archivo.set(input.files?.[0] ?? null);
    this.reporte.set(null);
    this.error.set(null);
  }

  protected async importar(): Promise<void> {
    const archivo = this.archivo();
    if (!archivo) return;
    this.ocupado.set(true);
    this.error.set(null);
    const r = await this.api.importar(archivo, this.listaId ? Number(this.listaId) : null);
    this.ocupado.set(false);
    if (r.ok) {
      this.reporte.set(r.valor);
    } else {
      this.error.set(r.mensaje);
    }
  }
}
