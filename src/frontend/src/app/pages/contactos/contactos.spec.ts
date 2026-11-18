import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Importar } from '../importar/importar';
import { Mensajes } from '../mensajes/mensajes';
import { Contacto, Conversacion, Importacion } from './contactos.service';
import { Contactos } from './contactos';
import { ConfirmacionService } from '../../core/confirmacion';

const ABIERTA: Conversacion = {
  contactoId: 1,
  telefono: '+59170000001',
  nombre: 'Ana',
  tipo: 'text',
  texto: 'Hola',
  fechaUtc: '2026-11-17T12:00:00Z',
  ventanaHastaUtc: '2026-11-18T11:50:00Z',
  consentimiento: 'SinConsentimiento',
  puedeOfrecer: true,
  motivo: null,
  ultimaOferta: null,
  respuesta: null,
};
const YA_ACEPTO: Conversacion = {
  ...ABIERTA,
  contactoId: 2,
  nombre: 'Beto',
  consentimiento: 'OptIn',
  puedeOfrecer: false,
  motivo: 'El contacto ya acepta promociones.',
  ultimaOferta: 'Enviada',
  respuesta: 'Si',
};
const ANA: Contacto = {
  id: 1,
  telefono: '+59170000001',
  nombre: 'Ana',
  clienteId: 'C-1',
  activo: true,
  consentimiento: 'SinConsentimiento',
  bajaPlataforma: false,
  ultimoMensajeUtc: null,
};

/** Deja correr las promesas pendientes y vuelve a pintar (sin zone.js, whenStable no espera los await). */
async function estable(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((r) => setTimeout(r, 0));
  fixture.detectChanges();
}

function configurar(): HttpTestingController {
  TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
  return TestBed.inject(HttpTestingController);
}

describe('Mensajes recientes', () => {
  let http: HttpTestingController;
  afterEach(() => http.verify());

  it('solo ofrece a quien tiene la ventana abierta y sin consentimiento; a los demás les dice por qué', async () => {
    http = configurar();
    const fixture = TestBed.createComponent(Mensajes);
    fixture.detectChanges();
    http.expectOne('/api/mensajes').flush([ABIERTA, YA_ACEPTO]);
    await estable(fixture);
    const filas = (fixture.nativeElement as HTMLElement).querySelectorAll('tbody tr');

    expect(filas[0].querySelector('button')?.textContent).toContain('Ofrecer promociones');
    expect(filas[1].querySelector('button')).toBeNull();
    expect(filas[1].textContent).toContain('ya acepta promociones');
    expect(filas[1].textContent).toContain('Respondió: Sí');
    fixture.destroy();
  });

  it('ofrecer pide confirmación, llama a la Api, muestra el resultado y recarga', async () => {
    http = configurar();
    const fixture = TestBed.createComponent(Mensajes);
    fixture.detectChanges();
    http.expectOne('/api/mensajes').flush([ABIERTA]);
    await estable(fixture);
    const confirmar = vi.spyOn(TestBed.inject(ConfirmacionService), 'preguntar').mockResolvedValue(true);

    (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('tbody button')!.click();
    await estable(fixture);
    http.expectOne((r) => r.url === '/api/contactos/1/ofrecer' && r.method === 'POST').flush({ enviada: true, mensaje: 'Oferta enviada.' });
    await estable(fixture);
    http.expectOne('/api/mensajes').flush([{ ...ABIERTA, puedeOfrecer: false, motivo: 'Ya se le ofreció.', ultimaOferta: 'Enviada' }]);
    await estable(fixture);

    expect(confirmar).toHaveBeenCalled();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('.aviso')?.textContent).toContain('Oferta enviada.');
    expect(el.querySelector('tbody')?.textContent).toContain('Ya se le ofreció.');
    fixture.destroy();
  });
});

describe('Importar contactos', () => {
  let http: HttpTestingController;
  afterEach(() => http.verify());

  it('sube el archivo como multipart y muestra solo errores y advertencias', async () => {
    http = configurar();
    const fixture = TestBed.createComponent(Importar);
    fixture.detectChanges();
    http.expectOne('/api/listas').flush([]);
    await estable(fixture);
    const el = fixture.nativeElement as HTMLElement;
    const input = el.querySelector<HTMLInputElement>('input[type="file"]')!;
    const archivo = new File(['Telefono\n70000001\n123\n'], 'clientes.csv', { type: 'text/csv' });
    Object.defineProperty(input, 'files', { value: [archivo] });
    input.dispatchEvent(new Event('change'));
    await estable(fixture);

    el.querySelector<HTMLButtonElement>('button[type="submit"]')!.click();
    await estable(fixture);
    const req = http.expectOne((r) => r.url === '/api/contactos/importar' && r.method === 'POST');
    expect(req.request.body instanceof FormData).toBe(true);
    expect(((req.request.body as FormData).get('archivo') as File).name).toBe('clientes.csv');
    const reporte: Importacion = {
      archivo: 'clientes.csv', filas: 2, validas: 1, errores: 1, creados: 1, actualizados: 0, optIn: 0, optOut: 0, advertencias: 0,
      detalle: [
        { row: 2, phone: '+59170000001', result: 'Creado', message: 'Contacto nuevo.' },
        { row: 3, phone: '123', result: 'Error', message: '"123": sin código de país…' },
      ],
    };
    req.flush(reporte);
    await estable(fixture);

    const filas = el.querySelectorAll('tbody tr');
    expect(filas.length).toBe(1);
    expect(filas[0].textContent).toContain('sin código de país');
    expect(el.querySelector('.resumen')?.textContent).toContain('1con error');
  });
});

describe('Contactos', () => {
  let http: HttpTestingController;
  afterEach(() => http.verify());

  it('pagina de a 25: "1–25 de 30" y Siguiente muestra los 5 restantes (F1.8b)', async () => {
    http = configurar();
    const fixture = TestBed.createComponent(Contactos);
    fixture.detectChanges();
    const treinta = Array.from({ length: 30 }, (_, i) => ({ ...ANA, id: i + 1, nombre: `Cliente ${i + 1}` }));
    http.expectOne((r) => r.url === '/api/contactos').flush(treinta);
    http.expectOne('/api/listas').flush([]);
    await estable(fixture);
    const el = fixture.nativeElement as HTMLElement;

    expect(el.querySelectorAll('tbody tr').length).toBe(25);
    expect(el.querySelector('.paginador')?.textContent).toContain('1–25 de 30');
    Array.from(el.querySelectorAll('.paginador button')).find((b) => b.textContent?.trim() === 'Siguiente')!.click();
    await estable(fixture);

    expect(el.querySelectorAll('tbody tr').length).toBe(5);
    expect(el.querySelector('tbody')?.textContent).toContain('Cliente 30');
    expect(el.querySelector('.paginador')?.textContent).toContain('26–30 de 30');
  });

  it('registrar baja manual envía el motivo y recarga el detalle', async () => {
    http = configurar();
    const fixture = TestBed.createComponent(Contactos);
    fixture.detectChanges();
    http.expectOne((r) => r.url === '/api/contactos').flush([ANA]);
    http.expectOne('/api/listas').flush([]);
    await estable(fixture);
    const el = fixture.nativeElement as HTMLElement;
    const detalle = { contacto: ANA, userId: null, historial: [], mensajes: [], ofertas: [], listas: [] };

    Array.from(el.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Ver')!.click();
    await estable(fixture);
    http.expectOne('/api/contactos/1').flush(detalle);
    await estable(fixture);
    const motivo = el.querySelector<HTMLInputElement>('input[name="motivo"]')!;
    motivo.value = 'Llamó a la tienda';
    motivo.dispatchEvent(new Event('input'));
    await estable(fixture);
    Array.from(el.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Registrar baja')!.click();
    await estable(fixture);

    const baja = http.expectOne((r) => r.url === '/api/contactos/1/baja' && r.method === 'POST');
    expect(baja.request.body).toEqual({ motivo: 'Llamó a la tienda' });
    baja.flush(null, { status: 204, statusText: 'No Content' });
    await estable(fixture);
    http.expectOne((r) => r.url === '/api/contactos').flush([{ ...ANA, consentimiento: 'OptOut' }]);
    await estable(fixture);
    http.expectOne('/api/contactos/1').flush({ ...detalle, contacto: { ...ANA, consentimiento: 'OptOut' } });
    await estable(fixture);

    expect(el.querySelector('.aviso')?.textContent).toContain('Baja registrada para Ana');
    expect(el.querySelector('.detalle')?.textContent).toContain('Pidió la baja');
    expect(el.querySelector('input[name="motivo"]')).toBeNull();
  });
});
