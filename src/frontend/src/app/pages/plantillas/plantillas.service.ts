import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom, Observable } from 'rxjs';
import { Resultado } from '../contactos/contactos.service';

export interface Plantilla {
  id: number;
  metaId: string | null;
  nombre: string;
  idioma: string;
  categoria: string;
  estado: string;
  estadoUtc: string;
  motivo: string | null;
  ultimoEvento: string | null;
  calidad: string | null;
  categoriaNueva: string | null;
  cambioCategoriaUtc: string | null;
  sincronizadaUtc: string | null;
  creadaPor: string;
}

export interface PlantillaDetalle {
  plantilla: Plantilla;
  formatoParametros: string | null;
  encabezado: string | null;
  cuerpo: string | null;
  pie: string | null;
  botones: string[];
  parametros: string[];
}

export interface NuevaPlantilla {
  nombre: string;
  idioma: string;
  encabezado: string;
  cuerpo: string;
  pie: string;
  ejemplos: Record<string, string>;
}

export interface Sincronizacion {
  total: number;
  nuevas: number;
  actualizadas: number;
  noEncontradas: number;
}

/** Etiqueta en español de los estados de Meta y los propios del sistema. */
export const TEXTO_ESTADO: Record<string, string> = {
  APPROVED: 'Aprobada',
  PENDING: 'En revisión',
  REJECTED: 'Rechazada',
  PAUSED: 'Pausada',
  DISABLED: 'Desactivada',
  IN_APPEAL: 'En apelación',
  ARCHIVED: 'Archivada',
  DELETED: 'Borrada',
  PENDING_DELETION: 'Por borrarse',
  LIMIT_EXCEEDED: 'Límite excedido',
  CREANDO: 'Creando…',
  INCIERTA: 'Incierta (sincroniza)',
  ERROR_CREACION: 'No creada',
  NO_ENCONTRADA: 'No está en Meta',
};

/** Parámetros con nombre del texto, en orden y sin repetir: igual que TemplateRules.Parameters del backend. */
export function parametros(texto: string): string[] {
  const vistos: string[] = [];
  for (const m of texto.matchAll(/\{\{([^{}]+)\}\}/g)) {
    const p = m[1].trim();
    if (!vistos.includes(p)) vistos.push(p);
  }
  return vistos;
}

/** Llamadas a /api/plantillas (F1.5, rol Operador). La Api audita la creación y la sincronización. */
@Injectable({ providedIn: 'root' })
export class PlantillasService {
  private readonly http = inject(HttpClient);

  listar(): Promise<Plantilla[]> {
    return firstValueFrom(this.http.get<Plantilla[]>('/api/plantillas'));
  }

  detalle(id: number): Promise<PlantillaDetalle> {
    return firstValueFrom(this.http.get<PlantillaDetalle>(`/api/plantillas/${id}`));
  }

  botones(): Promise<{ interes: string; baja: string }> {
    return firstValueFrom(this.http.get<{ interes: string; baja: string }>('/api/plantillas/botones'));
  }

  sincronizar(): Promise<Resultado<Sincronizacion>> {
    return this.ejecutar(this.http.post<Sincronizacion>('/api/plantillas/sincronizar', null));
  }

  crear(p: NuevaPlantilla): Promise<Resultado<{ mensaje: string; plantilla: Plantilla | null }>> {
    return this.ejecutar(this.http.post<{ mensaje: string; plantilla: Plantilla | null }>('/api/plantillas', p));
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
