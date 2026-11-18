import { DatePipe } from '@angular/common';
import { Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  Consentimiento,
  Contacto,
  ContactoDetalle,
  ContactosService,
  Lista,
  TEXTO_CONSENTIMIENTO,
} from './contactos.service';

/** Contactos (F1.4): estado de consentimiento, detalle con historial, baja manual y agregar a una lista. */
@Component({
  selector: 'app-contactos',
  imports: [DatePipe, FormsModule],
  templateUrl: './contactos.html',
})
export class Contactos implements OnInit {
  private readonly api = inject(ContactosService);
  protected readonly estados = Object.entries(TEXTO_CONSENTIMIENTO) as [Consentimiento, string][];

  protected readonly contactos = signal<Contacto[] | null>(null);
  protected readonly listas = signal<Lista[]>([]);
  protected readonly errorCarga = signal<string | null>(null);
  protected readonly aviso = signal<{ tipo: 'ok' | 'error'; texto: string } | null>(null);
  protected readonly ocupado = signal(false);
  protected readonly elegidos = signal<ReadonlySet<number>>(new Set());
  protected readonly detalle = signal<ContactoDetalle | null>(null);

  protected filtro = { consentimiento: '', buscar: '' };
  protected listaDestino: number | null = null;
  protected motivo = '';

  async ngOnInit(): Promise<void> {
    await Promise.all([this.cargar(), this.cargarListas()]);
  }

  protected texto(c: Consentimiento): string {
    return TEXTO_CONSENTIMIENTO[c];
  }

  protected alternar(id: number): void {
    this.elegidos.update((s) => {
      const n = new Set(s);
      if (n.has(id)) n.delete(id);
      else n.add(id);
      return n;
    });
  }

  protected async buscar(): Promise<void> {
    this.elegidos.set(new Set());
    await this.cargar();
  }

  protected async ver(c: Contacto): Promise<void> {
    this.motivo = '';
    this.detalle.set(await this.api.detalle(c.id));
  }

  protected cerrar(): void {
    this.detalle.set(null);
  }

  protected async registrarBaja(d: ContactoDetalle): Promise<void> {
    this.ocupado.set(true);
    const r = await this.api.baja(d.contacto.id, this.motivo);
    this.ocupado.set(false);
    if (!r.ok) {
      this.aviso.set({ tipo: 'error', texto: r.mensaje });
      return;
    }

    this.aviso.set({ tipo: 'ok', texto: `Baja registrada para ${d.contacto.nombre ?? d.contacto.telefono}.` });
    this.motivo = '';
    await this.cargar();
    this.detalle.set(await this.api.detalle(d.contacto.id));
  }

  protected async agregarALista(): Promise<void> {
    const lista = this.listas().find((l) => l.id === Number(this.listaDestino));
    if (!lista) return;
    this.ocupado.set(true);
    const r = await this.api.agregarALista(lista.id, [...this.elegidos()]);
    this.ocupado.set(false);
    if (!r.ok) {
      this.aviso.set({ tipo: 'error', texto: r.mensaje });
      return;
    }

    this.aviso.set({ tipo: 'ok', texto: `${r.valor.agregados} contacto(s) agregados a "${lista.nombre}".` });
    this.elegidos.set(new Set());
    await this.cargarListas();
  }

  private async cargar(): Promise<void> {
    try {
      this.contactos.set(await this.api.listar(this.filtro.consentimiento, this.filtro.buscar));
      this.errorCarga.set(null);
    } catch {
      this.errorCarga.set('No se pudo cargar la lista de contactos.');
    }
  }

  private async cargarListas(): Promise<void> {
    try {
      this.listas.set(await this.api.listas());
    } catch {
      this.listas.set([]);
    }
  }
}
