import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Alertas } from './alertas';
import { Monitor } from './monitor';
import { MonitorService, Tablero } from './monitor.service';
import { Revision } from './revision';

const TABLERO: Tablero = {
  pausa: { enPausa: false, motivo: null, desdeUtc: null, alertasAbiertas: 0 },
  capacidad: { limite: 250, fuenteLimite: 'Meta (TIER_250)', presupuesto: 25, usados: 3, disponibles: 22 },
  campanias: [
    {
      campanaId: 7,
      nombre: 'Promo semana',
      estado: 'Running',
      version: 2,
      total: 5,
      enCola: 2,
      enviandose: 0,
      enEspera: 0,
      enviados: 2,
      entregados: 2,
      leidos: 1,
      fallidos: 0,
      revision: 0,
      vencidos: 0,
      omitidos: { LimiteFrecuencia: 1 },
    },
  ],
};

async function estable(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((r) => setTimeout(r, 0));
  fixture.detectChanges();
}

describe('Monitor, envíos inciertos y alertas', () => {
  let http: HttpTestingController;
  let empujar: ((t: Tablero) => void) | null;

  beforeEach(() => {
    empujar = null;
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
    // SignalR falso: guarda la función que recibe los tableros para "empujar" uno desde la prueba.
    const service = TestBed.inject(MonitorService);
    service.conectar = async (alRecibir, alCambiarEstado) => {
      empujar = alRecibir;
      alCambiarEstado(true);
      return async () => undefined;
    };
  });
  afterEach(() => http.verify());

  it('muestra el tablero y lo cambia cuando la Api empuja uno nuevo por SignalR', async () => {
    const fixture = TestBed.createComponent(Monitor);
    fixture.detectChanges();
    http.expectOne('/api/monitor').flush(TABLERO);
    await estable(fixture);
    await estable(fixture);

    const texto = () => (fixture.nativeElement as HTMLElement).textContent!;
    expect(texto()).toContain('En vivo');
    expect(texto()).toContain('22');
    expect(texto()).toContain('1 tope semanal');
    expect(texto()).toContain('Despacho activo');

    empujar!({
      ...TABLERO,
      pausa: { enPausa: true, motivo: 'Token', desdeUtc: '2026-11-18T14:00:00', alertasAbiertas: 1 },
      campanias: [{ ...TABLERO.campanias[0], enCola: 0, enviados: 4 }],
    });
    fixture.detectChanges();

    expect(texto()).toContain('EN PAUSA por Token');
    const fila = (fixture.nativeElement as HTMLElement).querySelector('tbody tr')!;
    expect(fila.querySelectorAll('td')[2].textContent?.trim()).toBe('4');
  });

  it('envíos inciertos: "No llegó" llama a la Api y recarga', async () => {
    const fixture = TestBed.createComponent(Revision);
    fixture.detectChanges();
    http.expectOne('/api/revision').flush([
      {
        trabajoId: 11,
        campanaId: 7,
        campana: 'Promo semana',
        telefono: '+59170000001',
        nombre: 'Ana',
        intentoId: 'x',
        inicioUtc: '2026-11-18T10:00:00',
        vencido: true,
        detalle: null,
      },
    ]);
    await estable(fixture);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('vencido');

    const noLlego = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('button')).find((b) =>
      b.textContent?.includes('No llegó'),
    )!;
    noLlego.click();
    http.expectOne({ method: 'POST', url: '/api/revision/11/no-llego' }).flush({ mensaje: 'Marcado como no enviado.' });
    await estable(fixture);
    http.expectOne('/api/revision').flush([]);
    await estable(fixture);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Marcado como no enviado');
  });

  it('alertas: reanudar llama a la Api y muestra el despacho activo', async () => {
    const fixture = TestBed.createComponent(Alertas);
    fixture.detectChanges();
    http.expectOne('/api/alertas').flush({
      pausa: { enPausa: true, motivo: 'Token', desdeUtc: '2026-11-18T14:00:00', alertasAbiertas: 1 },
      abiertas: [
        {
          id: 1,
          tipo: 'Token',
          clave: 'global',
          mensaje: 'Error 190',
          detalle: null,
          origen: 'Despachador',
          ocurrencias: 2,
          creadaUtc: '2026-11-18T14:00:00',
          ultimaUtc: '2026-11-18T14:05:00',
        },
      ],
    });
    await estable(fixture);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('rotarlo en user-secrets');

    const boton = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('button')).find((b) =>
      b.textContent?.includes('reanudar'),
    )!;
    boton.click();
    http.expectOne({ method: 'POST', url: '/api/alertas/reanudar' }).flush({ mensaje: 'Despacho reanudado por apro-1.' });
    await estable(fixture);
    http.expectOne('/api/alertas').flush({ pausa: { enPausa: false, motivo: null, desdeUtc: null, alertasAbiertas: 0 }, abiertas: [] });
    await estable(fixture);
    const texto = (fixture.nativeElement as HTMLElement).textContent!;
    expect(texto).toContain('Despacho reanudado por apro-1');
    expect(texto).toContain('Despacho activo');
  });
});
