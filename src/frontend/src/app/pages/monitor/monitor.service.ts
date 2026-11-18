import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { firstValueFrom, Observable } from 'rxjs';
import { Resultado } from '../contactos/contactos.service';

export interface Contadores {
  campanaId: number;
  nombre: string;
  estado: string;
  version: number | null;
  total: number;
  enCola: number;
  enviandose: number;
  enEspera: number;
  enviados: number;
  entregados: number;
  leidos: number;
  fallidos: number;
  revision: number;
  vencidos: number;
  omitidos: Record<string, number>;
}

export interface Pausa {
  enPausa: boolean;
  motivo: string | null;
  desdeUtc: string | null;
  alertasAbiertas: number;
}

export interface Tablero {
  pausa: Pausa;
  capacidad: { limite: number; fuenteLimite: string; presupuesto: number; usados: number; disponibles: number };
  campanias: Contadores[];
}

export interface Baja {
  telefono: string;
  nombre: string | null;
  capa: 'Empresa' | 'Plataforma';
  estado: string;
  fuente: string;
  desdeUtc: string;
  evidencia: string | null;
}

export interface Incierto {
  trabajoId: number;
  campanaId: number | null;
  campana: string | null;
  telefono: string;
  nombre: string | null;
  intentoId: string;
  inicioUtc: string;
  vencido: boolean;
  detalle: string | null;
}

export interface Alerta {
  id: number;
  tipo: string;
  clave: string;
  mensaje: string;
  detalle: string | null;
  origen: string;
  ocurrencias: number;
  creadaUtc: string;
  ultimaUtc: string;
}

/** Motivos de omisión en español (DispatchJob.MotivoOmision). */
export const TEXTO_OMISION: Record<string, string> = {
  SinConsentimiento: 'sin consentimiento',
  BajaEmpresa: 'pidió la baja',
  NoContactar: 'no contactar',
  BajaPlataforma: 'baja en WhatsApp',
  LimiteFrecuencia: 'tope semanal',
  Fatiga: 'fatiga (pausa 60 días)',
  FatigaExcluido: 'fatiga (excluido)',
  YaEnviado: 'ya recibió otra versión',
  VersionReemplazada: 'versión reemplazada',
  CampanaCancelada: 'campaña cancelada',
  ContactoInactivo: 'contacto inactivo',
  SinCampania: 'sin campaña',
};

/** Ruta del hub de SignalR (WaBulk.Api MonitorHub.Path); el proxy de desarrollo la manda a la Api con websockets. */
export const RUTA_HUB = '/api/hubs/monitor';

/** /api/monitor, /api/bajas, /api/revision (Operador) y /api/alertas (Aprobador), F1.8. */
@Injectable({ providedIn: 'root' })
export class MonitorService {
  private readonly http = inject(HttpClient);

  tablero(): Promise<Tablero> {
    return firstValueFrom(this.http.get<Tablero>('/api/monitor'));
  }

  /**
   * Abre la conexión de SignalR y llama a alRecibir con cada tablero que empuja la Api. Reconecta sola; devuelve la
   * función para cerrarla. Si no conecta, la pantalla sigue con "Actualizar".
   */
  async conectar(alRecibir: (t: Tablero) => void, alCambiarEstado: (conectado: boolean) => void): Promise<() => Promise<void>> {
    const conexion: HubConnection = new HubConnectionBuilder()
      .withUrl(RUTA_HUB)
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();
    conexion.on('tablero', alRecibir);
    conexion.onreconnecting(() => alCambiarEstado(false));
    conexion.onreconnected(() => alCambiarEstado(true));
    conexion.onclose(() => alCambiarEstado(false));
    try {
      await conexion.start();
      alCambiarEstado(true);
    } catch {
      alCambiarEstado(false);
    }

    return async () => {
      if (conexion.state !== HubConnectionState.Disconnected) await conexion.stop();
    };
  }

  bajas(): Promise<Baja[]> {
    return firstValueFrom(this.http.get<Baja[]>('/api/bajas'));
  }

  inciertos(): Promise<Incierto[]> {
    return firstValueFrom(this.http.get<Incierto[]>('/api/revision'));
  }

  marcar(trabajoId: number, llego: boolean): Promise<Resultado<{ mensaje: string }>> {
    return this.ejecutar(this.http.post<{ mensaje: string }>(`/api/revision/${trabajoId}/${llego ? 'llego' : 'no-llego'}`, null));
  }

  alertas(): Promise<{ pausa: Pausa; abiertas: Alerta[] }> {
    return firstValueFrom(this.http.get<{ pausa: Pausa; abiertas: Alerta[] }>('/api/alertas'));
  }

  reanudar(): Promise<Resultado<{ mensaje: string }>> {
    return this.ejecutar(this.http.post<{ mensaje: string }>('/api/alertas/reanudar', null));
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
