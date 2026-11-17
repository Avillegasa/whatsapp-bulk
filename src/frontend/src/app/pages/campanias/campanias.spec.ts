import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Aprobaciones } from '../aprobaciones/aprobaciones';
import { Campanias } from './campanias';
import { Campana, CampanaDetalle, utc, vistaPrevia } from './campanias.service';

const CAMPANA: Campana = {
  id: 7,
  nombre: 'Promo semana',
  estado: 'PendingApproval',
  estadoUtc: '2026-11-18T14:00:00',
  motivo: null,
  version: 2,
  plantilla: 'promo_f15',
  idioma: 'es',
  destinatarios: 3,
  programadaUtc: '2026-11-18T14:00:00',
  expiraUtc: '2026-11-20T14:00:00',
  creadaPor: 'ana',
  creadaUtc: '2026-11-18T13:00:00',
};
const DETALLE: CampanaDetalle = {
  campana: CAMPANA,
  versiones: [
    {
      id: 21,
      numero: 2,
      plantillaId: 1,
      plantilla: 'promo_f15',
      idioma: 'es',
      categoria: 'MARKETING',
      formatoParametros: 'NAMED',
      listaId: 4,
      lista: 'Demo',
      destinatarios: 3,
      excluidos: 1,
      parametros: [
        { nombre: 'nombre', fuente: 'NombreContacto', valor: 'cliente' },
        { nombre: 'promocion', fuente: 'Fijo', valor: '2x1' },
      ],
      programadaUtc: '2026-11-18T14:00:00',
      expiraUtc: '2026-11-20T14:00:00',
      hash: 'a'.repeat(64),
      creadaUtc: '2026-11-18T13:00:00',
      creadaPor: 'ana',
      enviadaAprobacionUtc: '2026-11-18T13:05:00',
      aprobadaUtc: null,
      aprobadaPor: null,
      rechazadaUtc: null,
      rechazadaPor: null,
      motivoRechazo: null,
    },
  ],
  envios: [],
  encabezado: null,
  cuerpo: 'Hola {{nombre}}, esta semana {{promocion}}.',
  pie: null,
  botones: ['Me interesa', 'No recibir más'],
};

async function estable(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((r) => setTimeout(r, 0));
  fixture.detectChanges();
}

describe('Campañas y aprobaciones', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  it('utc toma las fechas de la Api (sin Z) como UTC y vistaPrevia muestra los parámetros', () => {
    expect(utc('2026-11-18T14:00:00')!.toISOString()).toBe('2026-11-18T14:00:00.000Z');
    expect(utc('2026-11-18T14:00:00Z')!.toISOString()).toBe('2026-11-18T14:00:00.000Z');
    expect(vistaPrevia(DETALLE.cuerpo, DETALLE.versiones[0].parametros)).toBe(
      'Hola [nombre del contacto o "cliente"], esta semana 2x1.',
    );
  });

  it('el aprobador ve el mensaje y aprueba la versión que vio (id y hash)', async () => {
    const fixture = TestBed.createComponent(Aprobaciones);
    fixture.detectChanges();
    http.expectOne('/api/aprobaciones').flush([CAMPANA]);
    await estable(fixture);

    (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('tbody button')!.click();
    http.expectOne('/api/aprobaciones/7').flush(DETALLE);
    await estable(fixture);
    const texto = (fixture.nativeElement as HTMLElement).textContent!;
    expect(texto).toContain('esta semana 2x1');
    expect(texto).toContain('1 quedaron fuera');

    const aprobar = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('button')).find((b) =>
      b.textContent?.includes('Aprobar versión 2'),
    )!;
    aprobar.click();
    const req = http.expectOne({ method: 'POST', url: '/api/aprobaciones/7/aprobar' });
    expect(req.request.body).toEqual({ versionId: 21, hash: 'a'.repeat(64) });
    req.flush({ mensaje: 'Versión 2 aprobada: 2 envíos en cola, 1 omitidos.' }, { status: 200, statusText: 'OK' });
    await estable(fixture);
    http.expectOne('/api/aprobaciones').flush([]);
    await estable(fixture);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('2 envíos en cola');
  });

  it('el 403 de cuatro ojos se muestra con el mensaje de la Api', async () => {
    const fixture = TestBed.createComponent(Aprobaciones);
    fixture.detectChanges();
    http.expectOne('/api/aprobaciones').flush([CAMPANA]);
    await estable(fixture);
    (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('tbody button')!.click();
    http.expectOne('/api/aprobaciones/7').flush(DETALLE);
    await estable(fixture);

    const c = fixture.componentInstance as unknown as { aprobar(d: CampanaDetalle): Promise<void> };
    const hecho = c.aprobar(DETALLE);
    http
      .expectOne('/api/aprobaciones/7/aprobar')
      .flush({ error: 'Prohibido', mensaje: 'No puedes aprobar una campaña que creaste o editaste.' }, { status: 403, statusText: 'Forbidden' });
    await estable(fixture);
    http.expectOne('/api/aprobaciones').flush([CAMPANA]);
    await hecho;
    await estable(fixture);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('No puedes aprobar una campaña que creaste');
  });

  it('al elegir la plantilla arma un parámetro por cada {{…}} y envía las fechas en UTC', async () => {
    const fixture = TestBed.createComponent(Campanias);
    fixture.detectChanges();
    http.expectOne('/api/campanias').flush([]);
    await estable(fixture);
    const c = fixture.componentInstance as unknown as {
      nueva(): Promise<void>;
      form: { nombre: string; plantillaId: number | null; listaId: number | null; parametros: { nombre: string; fuente: string; valor: string }[]; programada: string; expira: string };
      elegirPlantilla(): void;
      guardar(): Promise<void>;
    };

    const abierta = c.nueva();
    http.expectOne('/api/campanias/opciones').flush({
      plantillas: [{ id: 1, nombre: 'promo_f15', idioma: 'es', estado: 'APPROVED', parametros: ['nombre', 'promocion'] }],
      listas: [{ id: 4, nombre: 'Demo', miembros: 4 }],
    });
    await abierta;
    c.form.nombre = 'Promo';
    c.form.plantillaId = 1;
    c.elegirPlantilla();
    expect(c.form.parametros.map((p) => p.fuente)).toEqual(['NombreContacto', 'Fijo']);
    c.form.parametros[1].valor = '2x1';
    c.form.listaId = 4;
    c.form.programada = '2026-11-18T10:00';
    c.form.expira = '2026-11-20T10:00';

    const guardado = c.guardar();
    const req = http.expectOne({ method: 'POST', url: '/api/campanias' });
    expect(req.request.body.programadaUtc).toBe(new Date('2026-11-18T10:00').toISOString());
    expect(req.request.body.parametros[1]).toEqual({ nombre: 'promocion', fuente: 'Fijo', valor: '2x1' });
    req.flush({ mensaje: 'Campaña creada', id: 7 });
    await estable(fixture);
    http.expectOne('/api/campanias').flush([CAMPANA]);
    await estable(fixture);
    http.expectOne('/api/campanias/7').flush({ ...DETALLE, campana: { ...CAMPANA, estado: 'Draft' } });
    await guardado;
    await estable(fixture);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Enviar a aprobación');
  });
});
