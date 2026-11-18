import { Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Consentimiento, Contacto, ContactosService, Lista, TEXTO_CONSENTIMIENTO } from '../contactos/contactos.service';
import { enfocarDetalle } from '../../layout/foco';

/** Listas estáticas de contactos (F1.4): crear, ver miembros y quitar. Se agregan desde Contactos o al importar. */
@Component({
  selector: 'app-listas',
  imports: [FormsModule],
  templateUrl: './listas.html',
})
export class Listas implements OnInit {
  private readonly api = inject(ContactosService);
  protected readonly listas = signal<Lista[] | null>(null);
  protected readonly abierta = signal<Lista | null>(null);
  protected readonly miembros = signal<Contacto[]>([]);
  protected readonly aviso = signal<{ tipo: 'ok' | 'error'; texto: string } | null>(null);
  protected readonly ocupado = signal(false);
  protected nueva = { nombre: '', descripcion: '' };

  async ngOnInit(): Promise<void> {
    await this.cargar();
  }

  protected texto(c: Consentimiento): string {
    return TEXTO_CONSENTIMIENTO[c];
  }

  protected async crear(): Promise<void> {
    this.ocupado.set(true);
    const r = await this.api.crearLista(this.nueva.nombre.trim(), this.nueva.descripcion.trim());
    this.ocupado.set(false);
    if (!r.ok) {
      this.aviso.set({ tipo: 'error', texto: r.mensaje });
      return;
    }

    this.aviso.set({ tipo: 'ok', texto: `Lista "${r.valor.nombre}" creada.` });
    this.nueva = { nombre: '', descripcion: '' };
    await this.cargar();
  }

  protected async ver(l: Lista): Promise<void> {
    this.abierta.set(l);
    this.miembros.set(await this.api.miembros(l.id));
    enfocarDetalle();
  }

  protected async quitar(l: Lista, c: Contacto): Promise<void> {
    this.ocupado.set(true);
    const r = await this.api.quitarDeLista(l.id, c.id);
    this.ocupado.set(false);
    if (!r.ok) {
      this.aviso.set({ tipo: 'error', texto: r.mensaje });
      return;
    }

    this.aviso.set({ tipo: 'ok', texto: `${c.nombre ?? c.telefono} quitado de "${l.nombre}".` });
    await Promise.all([this.ver(l), this.cargar()]);
  }

  private async cargar(): Promise<void> {
    try {
      this.listas.set(await this.api.listas());
    } catch {
      this.aviso.set({ tipo: 'error', texto: 'No se pudieron cargar las listas.' });
    }
  }
}
