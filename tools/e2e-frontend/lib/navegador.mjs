// Edge sin ventana manejado por el protocolo DevTools (CDP), sin dependencias: WebSocket y fetch de Node 22+.
// Da lo mínimo para una prueba de punta a punta: abrir rutas, esperar condiciones, hacer clic, escribir,
// elegir archivos, emular un teléfono o el tema oscuro, sacar capturas y juntar los errores de la consola.
import { spawn } from 'node:child_process';
import { existsSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const RUTAS_EDGE = [
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  'C:/Program Files/Microsoft/Edge/Application/msedge.exe',
  'C:/Program Files/Google/Chrome/Application/chrome.exe',
];

const espera = (ms) => new Promise((r) => setTimeout(r, ms));

export class Navegador {
  /** @param {{ perfil: string, puerto?: number, base: string, interceptarApi?: (metodo: string, ruta: string) => unknown }} o */
  static async abrir(o) {
    const exe = RUTAS_EDGE.find((r) => existsSync(r));
    if (!exe) throw new Error('No se encontró Edge ni Chrome.');
    const puerto = o.puerto ?? 9333;
    const proceso = spawn(
      exe,
      ['--headless=new', '--disable-gpu', '--hide-scrollbars', '--no-first-run', '--no-default-browser-check',
        `--remote-debugging-port=${puerto}`, `--user-data-dir=${o.perfil}`, 'about:blank'],
      { stdio: 'ignore' },
    );
    let destino;
    for (let i = 0; i < 60 && !destino; i++) {
      await espera(250);
      try {
        destino = (await (await fetch(`http://127.0.0.1:${puerto}/json/list`)).json()).find((t) => t.type === 'page');
      } catch {
        // Todavía arrancando.
      }
    }
    if (!destino) throw new Error('El navegador no abrió el puerto de depuración.');
    const n = new Navegador(proceso, destino.webSocketDebuggerUrl, o);
    await n.conectar();
    return n;
  }

  constructor(proceso, url, o) {
    this.proceso = proceso;
    this.url = url;
    this.base = o.base;
    this.interceptarApi = o.interceptarApi;
    this.errores = [];
    this.respuestas5xx = [];
    this.id = 0;
    this.pendientes = new Map();
  }

  async conectar() {
    this.ws = new WebSocket(this.url);
    await new Promise((r, x) => {
      this.ws.addEventListener('open', r);
      this.ws.addEventListener('error', x);
    });
    this.ws.addEventListener('message', (e) => this.recibir(JSON.parse(e.data)));
    await this.cmd('Page.enable');
    await this.cmd('Runtime.enable');
    await this.cmd('Network.enable');
    await this.cmd('DOM.enable');
    if (this.interceptarApi) await this.cmd('Fetch.enable', { patterns: [{ urlPattern: '*/api/*' }] });
  }

  cmd(method, params = {}) {
    return new Promise((resolve, reject) => {
      const id = ++this.id;
      this.pendientes.set(id, { resolve, reject, method });
      this.ws.send(JSON.stringify({ id, method, params }));
    });
  }

  async recibir(m) {
    if (m.id && this.pendientes.has(m.id)) {
      const p = this.pendientes.get(m.id);
      this.pendientes.delete(m.id);
      if (m.error) p.reject(new Error(`${p.method}: ${m.error.message}`));
      else p.resolve(m.result);
      return;
    }
    if (m.method === 'Runtime.exceptionThrown') {
      const d = m.params.exceptionDetails;
      this.errores.push(`Excepción: ${d.exception?.description ?? d.text}`);
    } else if (m.method === 'Runtime.consoleAPICalled' && m.params.type === 'error') {
      this.errores.push(`console.error: ${m.params.args.map((a) => a.value ?? a.description).join(' ')}`);
    } else if (m.method === 'Network.responseReceived' && m.params.response.status >= 500) {
      this.respuestas5xx.push(`${m.params.response.status} ${m.params.response.url}`);
    } else if (m.method === 'Fetch.requestPaused' && this.interceptarApi) {
      const u = new URL(m.params.request.url);
      const cuerpo = this.interceptarApi(m.params.request.method, u.pathname + u.search);
      await this.cmd('Fetch.fulfillRequest', {
        requestId: m.params.requestId,
        responseCode: cuerpo === null ? 404 : 200,
        responseHeaders: [{ name: 'Content-Type', value: 'application/json' }],
        body: Buffer.from(JSON.stringify(cuerpo ?? {})).toString('base64'),
      });
    }
  }

  /** Evalúa una expresión en la página (puede devolver una promesa) y devuelve su valor. */
  async eval(expresion) {
    const r = await this.cmd('Runtime.evaluate', { expression: expresion, awaitPromise: true, returnByValue: true });
    if (r.exceptionDetails) throw new Error(`eval: ${r.exceptionDetails.exception?.description ?? r.exceptionDetails.text}`);
    return r.result.value;
  }

  /** Espera hasta que la expresión sea verdadera (o vence el plazo con un error que dice qué esperaba). */
  async esperar(expresion, { ms = 10_000, que = expresion } = {}) {
    const fin = Date.now() + ms;
    let ultimo;
    while (Date.now() < fin) {
      try {
        ultimo = await this.eval(expresion);
        if (ultimo) return ultimo;
      } catch (e) {
        ultimo = e.message;
      }
      await espera(150);
    }
    throw new Error(`No se cumplió en ${ms} ms: ${que} (último valor: ${JSON.stringify(ultimo)})`);
  }

  async ir(ruta) {
    await this.cmd('Page.navigate', { url: this.base + ruta });
    await this.esperar(`document.readyState === 'complete' && !!document.querySelector('app-root')?.children.length`, {
      que: `cargar ${ruta}`,
    });
  }

  async ancho(px, { movil = px < 600 } = {}) {
    await this.cmd('Emulation.setDeviceMetricsOverride', { width: px, height: movil ? 844 : 900, deviceScaleFactor: 1, mobile: movil });
  }

  async temaSistema(oscuro) {
    await this.cmd('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-color-scheme', value: oscuro ? 'dark' : 'light' }] });
  }

  /** Texto visible de toda la página (o de un selector). */
  texto(selector = 'body') {
    return this.eval(`document.querySelector(${JSON.stringify(selector)})?.innerText ?? ''`);
  }

  /** Clic en el botón o enlace cuyo texto (sin espacios de más) es exactamente `texto`, dentro de `dentro` (por defecto, main). */
  async clicTexto(texto, { dentro = null, etiqueta = 'button, a', ms = 10_000 } = {}) {
    // Sin `dentro`, busca en el contenido de la pantalla (main), no en el menú lateral.
    const js = `(() => {
      const raiz = ${dentro ? `document.querySelector(${JSON.stringify(dentro)})` : `(document.querySelector('main') ?? document.body)`};
      const el = raiz && Array.from(raiz.querySelectorAll(${JSON.stringify(etiqueta)}))
        .find((b) => b.textContent.replace(/\\s+/g, ' ').trim() === ${JSON.stringify(texto)} && !b.disabled);
      if (!el) return false;
      el.click();
      return true;
    })()`;
    await this.esperar(js, { ms, que: `clic en «${texto}»` });
  }

  async clic(selector, { ms = 10_000 } = {}) {
    await this.esperar(
      `(() => { const el = document.querySelector(${JSON.stringify(selector)}); if (!el || el.disabled) return false; el.click(); return true; })()`,
      { ms, que: `clic en ${selector}` },
    );
  }

  /**
   * Escribe en un campo (input/textarea) como lo haría una persona: valor + evento input + blur.
   * `selector` es un selector CSS o { etiqueta: 'texto' } para el campo dentro de la <label> o <fieldset> cuyo texto
   * empieza así (los campos con [name] dinámico de ngModel no tienen atributo name en el HTML).
   */
  async escribir(selector, valor) {
    const buscar =
      typeof selector === 'string'
        ? `document.querySelector(${JSON.stringify(selector)})`
        : `Array.from(document.querySelectorAll('main label, main fieldset'))
            .find((l) => l.innerText.trim().startsWith(${JSON.stringify(selector.etiqueta)}))
            ?.querySelector('input:not([type=radio]):not([type=checkbox]), textarea')`;
    await this.esperar(
      `(() => {
        const el = ${buscar};
        if (!el) return false;
        el.focus();
        const proto = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
        Object.getOwnPropertyDescriptor(proto, 'value').set.call(el, ${JSON.stringify(valor)});
        el.dispatchEvent(new Event('input', { bubbles: true }));
        el.dispatchEvent(new Event('change', { bubbles: true }));
        el.dispatchEvent(new Event('blur', { bubbles: true }));
        return true;
      })()`,
      { que: `escribir en ${JSON.stringify(selector)}` },
    );
  }

  /** Elige en un <select> la opción cuyo texto contiene `texto`. */
  async elegir(selector, texto) {
    await this.esperar(
      `(() => {
        const s = document.querySelector(${JSON.stringify(selector)});
        if (!s) return false;
        const i = Array.from(s.options).findIndex((o) => o.textContent.includes(${JSON.stringify(texto)}));
        if (i < 0) return false;
        s.selectedIndex = i;
        s.dispatchEvent(new Event('change', { bubbles: true }));
        return true;
      })()`,
      { que: `elegir «${texto}» en ${selector}` },
    );
  }

  async elegirArchivo(selector, ruta) {
    const { result } = await this.cmd('Runtime.evaluate', { expression: `document.querySelector(${JSON.stringify(selector)})` });
    await this.cmd('DOM.setFileInputFiles', { files: [ruta], objectId: result.objectId });
  }

  async tecla(key, code = key, keyCode = 0) {
    await this.cmd('Input.dispatchKeyEvent', { type: 'keyDown', key, code, windowsVirtualKeyCode: keyCode });
    await this.cmd('Input.dispatchKeyEvent', { type: 'keyUp', key, code, windowsVirtualKeyCode: keyCode });
  }

  async captura(carpeta, nombre) {
    const { cssContentSize } = await this.cmd('Page.getLayoutMetrics');
    const { cssVisualViewport } = await this.cmd('Page.getLayoutMetrics');
    const r = await this.cmd('Page.captureScreenshot', {
      format: 'png',
      captureBeyondViewport: true,
      clip: { x: 0, y: 0, width: cssVisualViewport.clientWidth, height: Math.min(Math.ceil(cssContentSize.height), 4000), scale: 1 },
    });
    const ruta = join(carpeta, `${nombre}.png`);
    writeFileSync(ruta, Buffer.from(r.data, 'base64'));
    return ruta;
  }

  async cerrar() {
    try {
      this.ws?.close();
    } finally {
      this.proceso.kill();
    }
  }
}
