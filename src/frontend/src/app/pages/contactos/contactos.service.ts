import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom, Observable } from 'rxjs';

export type Consentimiento = 'OptIn' | 'OptOut' | 'NoContactar' | 'SinConsentimiento';

export interface Contacto {
  id: number;
  telefono: string | null;
  nombre: string | null;
  clienteId: string | null;
  activo: boolean;
  consentimiento: Consentimiento;
  bajaPlataforma: boolean;
  ultimoMensajeUtc: string | null;
}

export interface ContactoDetalle {
  contacto: Contacto;
  userId: string | null;
  historial: { recordedUtc: string; status: string; source: string; evidence: string | null; recordedBy: string }[];
  mensajes: { id: number; sentUtc: string; type: string; text: string | null; buttonId: string | null }[];
  ofertas: {
    id: number;
    offeredUtc: string;
    offeredBy: string;
    textVersion: string;
    status: string;
    response: string | null;
    responseUtc: string | null;
  }[];
  listas: string[];
}

export interface Conversacion {
  contactoId: number;
  telefono: string | null;
  nombre: string | null;
  tipo: string;
  texto: string | null;
  fechaUtc: string;
  ventanaHastaUtc: string;
  consentimiento: Consentimiento;
  puedeOfrecer: boolean;
  motivo: string | null;
  ultimaOferta: string | null;
  respuesta: string | null;
}

export interface Lista {
  id: number;
  nombre: string;
  descripcion: string | null;
  miembros: number;
  creadaUtc: string;
  creadaPor: string;
}

export interface FilaImportada {
  row: number;
  phone: string | null;
  result: 'Creado' | 'Actualizado' | 'Advertencia' | 'Error';
  message: string;
}

export interface Importacion {
  archivo: string;
  filas: number;
  validas: number;
  errores: number;
  creados: number;
  actualizados: number;
  optIn: number;
  optOut: number;
  advertencias: number;
  detalle: FilaImportada[];
}

/** Resultado de una acción: lo que devolvió la Api o su mensaje de error (ya en español). */
export type Resultado<T> = { ok: true; valor: T } | { ok: false; mensaje: string };

/** Etiqueta en español de cada estado de consentimiento. */
export const TEXTO_CONSENTIMIENTO: Record<Consentimiento, string> = {
  OptIn: 'Acepta promociones',
  OptOut: 'Pidió la baja',
  NoContactar: 'No contactar',
  SinConsentimiento: 'Sin consentimiento',
};

/** Llamadas a /api/mensajes, /api/contactos y /api/listas (F1.4, rol Operador). La Api audita cada cambio. */
@Injectable({ providedIn: 'root' })
export class ContactosService {
  private readonly http = inject(HttpClient);

  conversaciones(): Promise<Conversacion[]> {
    return firstValueFrom(this.http.get<Conversacion[]>('/api/mensajes'));
  }

  ofrecer(contactoId: number): Promise<Resultado<{ enviada: boolean; mensaje: string }>> {
    return this.ejecutar(this.http.post<{ enviada: boolean; mensaje: string }>(`/api/contactos/${contactoId}/ofrecer`, null));
  }

  listar(consentimiento: string, buscar: string): Promise<Contacto[]> {
    const params: Record<string, string> = {};
    if (consentimiento) params['consentimiento'] = consentimiento;
    if (buscar.trim()) params['buscar'] = buscar.trim();
    return firstValueFrom(this.http.get<Contacto[]>('/api/contactos', { params }));
  }

  detalle(id: number): Promise<ContactoDetalle> {
    return firstValueFrom(this.http.get<ContactoDetalle>(`/api/contactos/${id}`));
  }

  baja(id: number, motivo: string): Promise<Resultado<null>> {
    return this.ejecutar(this.http.post<null>(`/api/contactos/${id}/baja`, { motivo }));
  }

  importar(archivo: File, listaId: number | null): Promise<Resultado<Importacion>> {
    const form = new FormData();
    form.append('archivo', archivo, archivo.name);
    const params: Record<string, string> = listaId ? { lista: String(listaId) } : {};
    return this.ejecutar(this.http.post<Importacion>('/api/contactos/importar', form, { params }));
  }

  listas(): Promise<Lista[]> {
    return firstValueFrom(this.http.get<Lista[]>('/api/listas'));
  }

  crearLista(nombre: string, descripcion: string): Promise<Resultado<Lista>> {
    return this.ejecutar(this.http.post<Lista>('/api/listas', { nombre, descripcion }));
  }

  miembros(listaId: number): Promise<Contacto[]> {
    return firstValueFrom(this.http.get<Contacto[]>(`/api/listas/${listaId}/miembros`));
  }

  agregarALista(listaId: number, contactos: number[]): Promise<Resultado<{ agregados: number }>> {
    return this.ejecutar(this.http.post<{ agregados: number }>(`/api/listas/${listaId}/miembros`, { contactos }));
  }

  quitarDeLista(listaId: number, contactoId: number): Promise<Resultado<null>> {
    return this.ejecutar(this.http.delete<null>(`/api/listas/${listaId}/miembros/${contactoId}`));
  }

  private async ejecutar<T>(peticion: Observable<T>): Promise<Resultado<T>> {
    try {
      return { ok: true, valor: await firstValueFrom(peticion) };
    } catch (e) {
      const mensaje =
        e instanceof HttpErrorResponse && typeof e.error?.mensaje === 'string'
          ? (e.error.mensaje as string)
          : e instanceof HttpErrorResponse && e.status === 403
            ? 'Tu usuario no tiene permiso para esta acción.'
            : 'No se pudo completar la acción. Intenta de nuevo.';
      return { ok: false, mensaje };
    }
  }
}
