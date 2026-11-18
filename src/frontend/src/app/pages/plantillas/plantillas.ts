import { DatePipe } from '@angular/common';
import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { NuevaPlantilla, parametros, Plantilla, PlantillaDetalle, PlantillasService, TEXTO_ESTADO } from './plantillas.service';

/**
 * Plantillas (F1.5): lista sincronizada con Meta, detalle y creación de plantillas de marketing (D-009) con parámetros
 * con nombre y los botones «Me interesa» / «No recibir más» (D-012). El estado cambia solo cuando llega el webhook de
 * Meta (el Worker lo procesa); "Actualizar" vuelve a leer la tabla y "Sincronizar con Meta" la trae de Meta.
 */
@Component({
  selector: 'app-plantillas',
  imports: [FormsModule, DatePipe],
  templateUrl: './plantillas.html',
})
export class Plantillas implements OnInit {
  private readonly api = inject(PlantillasService);
  protected readonly plantillas = signal<Plantilla[] | null>(null);
  protected readonly abierta = signal<PlantillaDetalle | null>(null);
  protected readonly aviso = signal<{ tipo: 'ok' | 'error'; texto: string } | null>(null);
  protected readonly ocupado = signal(false);
  protected readonly botones = signal<{ interes: string; baja: string } | null>(null);
  protected readonly creando = signal(false);

  protected nueva: NuevaPlantilla = Plantillas.vacia();
  /** El cuerpo como signal para que los campos de ejemplo sigan a los parámetros mientras se escribe. */
  protected readonly cuerpo = signal('');
  protected readonly params = computed(() => parametros(this.cuerpo()));

  async ngOnInit(): Promise<void> {
    await this.cargar();
    try {
      this.botones.set(await this.api.botones());
    } catch {
      // Sin botones no se muestra la vista previa; crear sigue funcionando.
    }
  }

  protected estado(e: string): string {
    return TEXTO_ESTADO[e] ?? e;
  }

  protected claseEstado(e: string): string {
    if (e === 'APPROVED') return 'ok';
    if (['REJECTED', 'PAUSED', 'DISABLED', 'ERROR_CREACION', 'NO_ENCONTRADA'].includes(e)) return 'mal';
    return 'espera';
  }

  protected cambiarCuerpo(texto: string): void {
    this.nueva.cuerpo = texto;
    this.cuerpo.set(texto);
  }

  /** Vista previa del cuerpo con los ejemplos en lugar de los parámetros. */
  protected vistaPrevia(): string {
    return this.nueva.cuerpo.replace(/\{\{([^{}]+)\}\}/g, (todo, p: string) => this.nueva.ejemplos[p.trim()] || todo);
  }

  protected async sincronizar(): Promise<void> {
    this.ocupado.set(true);
    const r = await this.api.sincronizar();
    this.ocupado.set(false);
    if (!r.ok) {
      this.aviso.set({ tipo: 'error', texto: r.mensaje });
      return;
    }

    const s = r.valor;
    this.aviso.set({
      tipo: 'ok',
      texto: `Sincronizado: ${s.total} en Meta, ${s.nuevas} nuevas, ${s.actualizadas} actualizadas, ${s.noEncontradas} ya no están.`,
    });
    await this.cargar();
  }

  protected async ver(p: Plantilla): Promise<void> {
    this.abierta.set(await this.api.detalle(p.id));
  }

  protected async crear(): Promise<void> {
    this.ocupado.set(true);
    const ejemplos: Record<string, string> = {};
    for (const p of this.params()) ejemplos[p] = this.nueva.ejemplos[p] ?? '';
    const r = await this.api.crear({ ...this.nueva, ejemplos });
    this.ocupado.set(false);
    if (!r.ok) {
      this.aviso.set({ tipo: 'error', texto: r.mensaje });
      await this.cargar(); // Un rechazo de Meta deja la fila "No creada" con el motivo.
      return;
    }

    this.aviso.set({ tipo: 'ok', texto: r.valor.mensaje });
    this.nueva = Plantillas.vacia();
    this.cuerpo.set('');
    this.creando.set(false);
    await this.cargar();
  }

  protected async cargar(): Promise<void> {
    try {
      this.plantillas.set(await this.api.listar());
      const abierta = this.abierta();
      if (abierta) this.abierta.set(await this.api.detalle(abierta.plantilla.id));
    } catch {
      this.aviso.set({ tipo: 'error', texto: 'No se pudieron cargar las plantillas.' });
    }
  }

  private static vacia(): NuevaPlantilla {
    return { nombre: '', idioma: 'es', encabezado: '', cuerpo: '', pie: '', ejemplos: {} };
  }
}
