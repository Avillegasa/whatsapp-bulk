import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom, Observable } from 'rxjs';
import { Resultado } from '../contactos/contactos.service';

export type EstadoCampana =
  | 'Draft'
  | 'PendingApproval'
  | 'Scheduled'
  | 'Running'
  | 'Paused'
  | 'DispatchCompleted'
  | 'TrackingClosed'
  | 'Cancelled'
  | 'Expired';

export interface Parametro {
  nombre: string;
  fuente: 'Fijo' | 'NombreContacto';
  valor: string;
}

export interface Campana {
  id: number;
  nombre: string;
  estado: EstadoCampana;
  estadoUtc: string;
  motivo: string | null;
  version: number | null;
  plantilla: string | null;
  idioma: string | null;
  destinatarios: number;
  programadaUtc: string | null;
  expiraUtc: string | null;
  creadaPor: string;
  creadaUtc: string;
}

export interface Version {
  id: number;
  numero: number;
  plantillaId: number;
  plantilla: string;
  idioma: string;
  categoria: string;
  formatoParametros: string;
  listaId: number | null;
  lista: string | null;
  destinatarios: number;
  excluidos: number;
  parametros: Parametro[];
  programadaUtc: string;
  expiraUtc: string;
  hash: string;
  creadaUtc: string;
  creadaPor: string;
  enviadaAprobacionUtc: string | null;
  aprobadaUtc: string | null;
  aprobadaPor: string | null;
  rechazadaUtc: string | null;
  rechazadaPor: string | null;
  motivoRechazo: string | null;
}

export interface CampanaDetalle {
  campana: Campana;
  versiones: Version[];
  envios: { estado: string; motivo: string | null; cantidad: number }[];
  encabezado: string | null;
  cuerpo: string | null;
  pie: string | null;
  botones: string[];
}

export interface Opciones {
  plantillas: { id: number; nombre: string; idioma: string; estado: string; parametros: string[] }[];
  listas: { id: number; nombre: string; miembros: number }[];
}

export interface CampanaRequest {
  nombre: string;
  plantillaId: number;
  listaId: number;
  parametros: Parametro[];
  programadaUtc: string;
  expiraUtc: string;
}

export const TEXTO_ESTADO_CAMPANA: Record<EstadoCampana, string> = {
  Draft: 'Borrador',
  PendingApproval: 'Por aprobar',
  Scheduled: 'Programada',
  Running: 'Enviando',
  Paused: 'En pausa',
  DispatchCompleted: 'Enviada',
  TrackingClosed: 'Cerrada',
  Cancelled: 'Cancelada',
  Expired: 'Vencida',
};

export const TEXTO_ENVIO: Record<string, string> = {
  Queued: 'En cola',
  Claimed: 'Enviándose',
  Submitted: 'Enviado',
  RetryScheduled: 'Reintento programado',
  NeedsReview: 'Revisar',
  Failed: 'Falló',
  Skipped: 'Omitido',
  Expired: 'Vencido',
  OnHold: 'En espera (pausa)',
};

/**
 * La Api manda las fechas UTC sin "Z" (SQL Server no guarda el tipo de fecha); sin esto, el navegador las tomaría como
 * hora local. Devuelve un Date correcto para el pipe date.
 */
export function utc(iso: string | null | undefined): Date | null {
  if (!iso) return null;
  return new Date(/[zZ]|[+-]\d\d:\d\d$/.test(iso) ? iso : iso + 'Z');
}

/** Reemplaza {{parametro}} por su valor de ejemplo (o el texto por defecto si sale del contacto). */
export function vistaPrevia(cuerpo: string | null, parametros: Parametro[]): string {
  return (cuerpo ?? '').replace(/\{\{([^{}]+)\}\}/g, (todo, p: string) => {
    const par = parametros.find((x) => x.nombre === p.trim());
    if (!par) return todo;
    return par.fuente === 'NombreContacto' ? `[nombre del contacto o "${par.valor}"]` : par.valor;
  });
}

/** /api/campanias (Operador) y /api/aprobaciones (Aprobador), F1.6. La Api audita cada acción. */
@Injectable({ providedIn: 'root' })
export class CampaniasService {
  private readonly http = inject(HttpClient);

  listar(): Promise<Campana[]> {
    return firstValueFrom(this.http.get<Campana[]>('/api/campanias'));
  }

  detalle(id: number): Promise<CampanaDetalle> {
    return firstValueFrom(this.http.get<CampanaDetalle>(`/api/campanias/${id}`));
  }

  opciones(): Promise<Opciones> {
    return firstValueFrom(this.http.get<Opciones>('/api/campanias/opciones'));
  }

  crear(r: CampanaRequest): Promise<Resultado<{ mensaje: string; id: number }>> {
    return this.ejecutar(this.http.post<{ mensaje: string; id: number }>('/api/campanias', r));
  }

  nuevaVersion(id: number, r: CampanaRequest): Promise<Resultado<{ mensaje: string; id: number }>> {
    return this.ejecutar(this.http.post<{ mensaje: string; id: number }>(`/api/campanias/${id}/versiones`, r));
  }

  accion(id: number, accion: 'enviar' | 'pausar' | 'reanudar' | 'cancelar', motivo?: string): Promise<Resultado<{ mensaje: string }>> {
    const body = accion === 'pausar' || accion === 'cancelar' ? { motivo: motivo ?? null } : null;
    return this.ejecutar(this.http.post<{ mensaje: string }>(`/api/campanias/${id}/${accion}`, body));
  }

  pendientes(): Promise<Campana[]> {
    return firstValueFrom(this.http.get<Campana[]>('/api/aprobaciones'));
  }

  detallePendiente(id: number): Promise<CampanaDetalle> {
    return firstValueFrom(this.http.get<CampanaDetalle>(`/api/aprobaciones/${id}`));
  }

  aprobar(id: number, versionId: number, hash: string): Promise<Resultado<{ mensaje: string }>> {
    return this.ejecutar(this.http.post<{ mensaje: string }>(`/api/aprobaciones/${id}/aprobar`, { versionId, hash }));
  }

  rechazar(id: number, versionId: number, motivo: string): Promise<Resultado<{ mensaje: string }>> {
    return this.ejecutar(this.http.post<{ mensaje: string }>(`/api/aprobaciones/${id}/rechazar`, { versionId, motivo }));
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
