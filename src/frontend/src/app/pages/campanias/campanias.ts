import { DatePipe } from '@angular/common';
import { Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  Campana,
  CampanaDetalle,
  CampaniasService,
  EstadoCampana,
  Opciones,
  Parametro,
  TEXTO_ENVIO,
  TEXTO_ESTADO_CAMPANA,
  utc,
  vistaPrevia,
} from './campanias.service';

interface Formulario {
  nombre: string;
  plantillaId: number | null;
  listaId: number | null;
  parametros: Parametro[];
  programada: string;
  expira: string;
}

/**
 * Campañas (F1.6, Operador): crear, editar (versión nueva), enviar a aprobación, pausar, reanudar y cancelar.
 * Las fechas se escriben en la hora local del navegador y viajan en UTC.
 */
@Component({
  selector: 'app-campanias',
  imports: [FormsModule, DatePipe],
  templateUrl: './campanias.html',
  styleUrls: ['../contactos/contactos.css', '../plantillas/plantillas.css', './campanias.css'],
})
export class Campanias implements OnInit {
  private readonly api = inject(CampaniasService);
  protected readonly campanias = signal<Campana[] | null>(null);
  protected readonly abierta = signal<CampanaDetalle | null>(null);
  protected readonly opciones = signal<Opciones | null>(null);
  protected readonly aviso = signal<{ tipo: 'ok' | 'error'; texto: string } | null>(null);
  protected readonly ocupado = signal(false);
  /** null = cerrado; 0 = campaña nueva; id = versión nueva de esa campaña. */
  protected readonly editando = signal<number | null>(null);
  protected form: Formulario = Campanias.vacio();

  async ngOnInit(): Promise<void> {
    await this.cargar();
  }

  protected estado(e: EstadoCampana): string {
    return TEXTO_ESTADO_CAMPANA[e] ?? e;
  }

  protected readonly fecha = utc;

  protected envio(e: string): string {
    return TEXTO_ENVIO[e] ?? e;
  }

  protected plantilla() {
    return this.opciones()?.plantillas.find((p) => p.id === Number(this.form.plantillaId)) ?? null;
  }

  /** Al elegir la plantilla, un parámetro por cada {{…}} del cuerpo; "nombre" sale del contacto por defecto. */
  protected elegirPlantilla(): void {
    const previos = this.form.parametros;
    this.form.parametros = (this.plantilla()?.parametros ?? []).map(
      (n) =>
        previos.find((p) => p.nombre === n) ?? {
          nombre: n,
          fuente: n.includes('nombre') ? 'NombreContacto' : 'Fijo',
          valor: n.includes('nombre') ? 'cliente' : '',
        },
    );
  }

  protected async nueva(): Promise<void> {
    await this.cargarOpciones();
    this.form = Campanias.vacio();
    this.editando.set(0);
  }

  protected async editar(d: CampanaDetalle): Promise<void> {
    await this.cargarOpciones();
    const v = d.versiones[0];
    this.form = {
      nombre: d.campana.nombre,
      plantillaId: v.plantillaId,
      listaId: v.listaId,
      parametros: v.parametros.map((p) => ({ ...p })),
      programada: Campanias.local(v.programadaUtc),
      expira: Campanias.local(v.expiraUtc),
    };
    this.editando.set(d.campana.id);
  }

  protected async guardar(): Promise<void> {
    const id = this.editando();
    if (id === null || !this.form.plantillaId || !this.form.listaId || !this.form.programada || !this.form.expira) return;
    const req = {
      nombre: this.form.nombre.trim(),
      plantillaId: Number(this.form.plantillaId),
      listaId: Number(this.form.listaId),
      parametros: this.form.parametros,
      programadaUtc: new Date(this.form.programada).toISOString(),
      expiraUtc: new Date(this.form.expira).toISOString(),
    };
    this.ocupado.set(true);
    const r = id === 0 ? await this.api.crear(req) : await this.api.nuevaVersion(id, req);
    this.ocupado.set(false);
    if (!r.ok) {
      this.aviso.set({ tipo: 'error', texto: r.mensaje });
      return;
    }

    this.aviso.set({ tipo: 'ok', texto: r.valor.mensaje });
    this.editando.set(null);
    await this.cargar();
    await this.ver(r.valor.id);
  }

  protected async accion(c: Campana, accion: 'enviar' | 'pausar' | 'reanudar' | 'cancelar'): Promise<void> {
    let motivo: string | undefined;
    if (accion === 'cancelar') {
      const m = prompt('¿Por qué se cancela? Lo pendiente no saldrá.');
      if (m === null) return;
      motivo = m;
    }

    this.ocupado.set(true);
    const r = await this.api.accion(c.id, accion, motivo);
    this.ocupado.set(false);
    this.aviso.set(r.ok ? { tipo: 'ok', texto: r.valor.mensaje } : { tipo: 'error', texto: r.mensaje });
    await this.cargar();
    await this.ver(c.id);
  }

  protected async ver(id: number): Promise<void> {
    try {
      this.abierta.set(await this.api.detalle(id));
    } catch {
      this.aviso.set({ tipo: 'error', texto: 'No se pudo cargar la campaña.' });
    }
  }

  protected previa(d: CampanaDetalle): string {
    return vistaPrevia(d.cuerpo, d.versiones[0]?.parametros ?? []);
  }

  protected async cargar(): Promise<void> {
    try {
      this.campanias.set(await this.api.listar());
    } catch {
      this.aviso.set({ tipo: 'error', texto: 'No se pudieron cargar las campañas.' });
    }
  }

  private async cargarOpciones(): Promise<void> {
    if (!this.opciones()) this.opciones.set(await this.api.opciones());
  }

  private static vacio(): Formulario {
    const ahora = new Date();
    return {
      nombre: '',
      plantillaId: null,
      listaId: null,
      parametros: [],
      programada: Campanias.local(ahora.toISOString()),
      expira: Campanias.local(new Date(ahora.getTime() + 2 * 86_400_000).toISOString()),
    };
  }

  /** ISO UTC → valor de <input type="datetime-local"> en la hora local. */
  private static local(iso: string): string {
    const d = utc(iso)!;
    const pad = (n: number) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
  }
}
