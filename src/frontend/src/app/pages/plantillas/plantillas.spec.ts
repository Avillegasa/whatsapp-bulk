import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Plantillas } from './plantillas';
import { parametros, Plantilla } from './plantillas.service';

const PROMO: Plantilla = {
  id: 1,
  metaId: '1501960108463090',
  nombre: 'promo_demo',
  idioma: 'es',
  categoria: 'MARKETING',
  estado: 'APPROVED',
  estadoUtc: '2026-11-17T12:00:00Z',
  motivo: null,
  ultimoEvento: 'APPROVED',
  calidad: 'GREEN',
  categoriaNueva: null,
  cambioCategoriaUtc: null,
  sincronizadaUtc: '2026-11-17T12:00:00Z',
  creadaPor: 'meta',
};
const RECHAZADA: Plantilla = { ...PROMO, id: 2, metaId: '2', nombre: 'promo_mala', estado: 'REJECTED', motivo: 'INVALID_FORMAT' };

async function estable(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((r) => setTimeout(r, 0));
  fixture.detectChanges();
}

describe('Plantillas', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  async function abrir(lista: Plantilla[]): Promise<ComponentFixture<Plantillas>> {
    const fixture = TestBed.createComponent(Plantillas);
    fixture.detectChanges();
    http.expectOne('/api/plantillas').flush(lista);
    await estable(fixture);
    http.expectOne('/api/plantillas/botones').flush({ interes: 'Me interesa', baja: 'No recibir más' });
    await estable(fixture);
    return fixture;
  }

  it('muestra cada plantilla con su estado en español y el motivo del rechazo', async () => {
    const fixture = await abrir([PROMO, RECHAZADA]);
    const filas = (fixture.nativeElement as HTMLElement).querySelectorAll('tbody tr');

    expect(filas.length).toBe(2);
    expect(filas[0].textContent).toContain('Aprobada');
    expect(filas[0].textContent).toContain('GREEN');
    expect(filas[1].textContent).toContain('Rechazada');
    expect(filas[1].textContent).toContain('INVALID_FORMAT');
  });

  it('sincronizar llama a la Api, informa el resultado y recarga', async () => {
    const fixture = await abrir([]);
    const boton = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('button')).find((b) =>
      b.textContent?.includes('Sincronizar'),
    )!;

    boton.click();
    http.expectOne({ method: 'POST', url: '/api/plantillas/sincronizar' }).flush({ total: 2, nuevas: 2, actualizadas: 0, noEncontradas: 0 });
    await estable(fixture);
    http.expectOne('/api/plantillas').flush([PROMO]);
    await estable(fixture);

    const texto = (fixture.nativeElement as HTMLElement).textContent!;
    expect(texto).toContain('2 nuevas');
    expect(texto).toContain('promo_demo');
  });

  it('crear manda solo los ejemplos de los parámetros del cuerpo', async () => {
    const fixture = await abrir([]);
    const c = fixture.componentInstance as unknown as {
      nueva: { nombre: string; ejemplos: Record<string, string> };
      cambiarCuerpo(t: string): void;
      crear(): Promise<void>;
    };
    c.nueva.nombre = 'promo_diciembre';
    c.cambiarCuerpo('Hola {{nombre}}, hoy {{promocion}}');
    c.nueva.ejemplos = { nombre: 'Ana', promocion: '2x1', viejo: 'x' };

    const creada = c.crear();
    const req = http.expectOne({ method: 'POST', url: '/api/plantillas' });
    expect(req.request.body.ejemplos).toEqual({ nombre: 'Ana', promocion: '2x1' });
    req.flush({ mensaje: 'Plantilla promo_diciembre enviada a Meta: estado PENDING.', plantilla: null });
    await estable(fixture);
    http.expectOne('/api/plantillas').flush([]);
    await creada;
    await estable(fixture);

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('estado PENDING');
  });

  it('parametros sigue el orden del texto sin repetir', () => {
    expect(parametros('{{a}} {{ b }} {{a}}')).toEqual(['a', 'b']);
  });
});
