// Prueba de punta a punta de la intranet (F1.8b): la app real (build de producción de Angular + Api + receptor de
// webhooks + Worker) contra una base TEMPORAL y un Meta FALSO. Una persona simulada con Edge recorre los flujos de la
// demo: login, usuarios, mensaje entrante → ofrecer promociones → «Sí», listas, importar, contactos, plantillas,
// campaña → aprobación de otra persona → despacho → monitor en vivo → freno de emergencia → reanudar → baja.
//
// Nada sale a WhatsApp (Meta:BaseUrl apunta al Meta falso) y la base WaBulk de desarrollo no se toca
// (WaBulk_E2E_<id>, se borra al final). Uso (cmd, desde la raíz del repo; SQL Server levantado):
//   node tools\e2e-frontend\e2e.mjs            → corre todo y deja el informe en %TEMP%\wabulk-e2e\<id>\
//   node tools\e2e-frontend\e2e.mjs --mantener → no borra la base ni detiene los procesos al final (para mirar)
//   node tools\e2e-frontend\e2e.mjs --sin-compilar → reutiliza la última compilación (backend y frontend)
import { spawn, spawnSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, writeFileSync, createWriteStream } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { iniciarMetaFalso, PLANTILLA_E2E } from './lib/meta-falso.mjs';
import { Navegador } from './lib/navegador.mjs';
import { iniciarServidor } from './lib/servidor.mjs';
import { crearWebhooks } from './lib/webhooks.mjs';

const RAIZ = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const MANTENER = process.argv.includes('--mantener');
const SIN_COMPILAR = process.argv.includes('--sin-compilar');
const ID = new Date().toISOString().replace(/\D/g, '').slice(0, 14);
const BASE_DATOS = `WaBulk_E2E_${ID}`;
const TMP = join(tmpdir(), 'wabulk-e2e');
const SALIDA = join(TMP, ID);
const BUILD = join(TMP, 'build');
const PUERTOS = { api: 5097, webhooks: 5098, meta: 5099, web: 4300, cdp: 9334 };
const APP_SECRET = 'e2e-app-secret-' + randomBytes(8).toString('hex');
mkdirSync(SALIDA, { recursive: true });

// ---------- utilidades ----------
const espera = (ms) => new Promise((r) => setTimeout(r, ms));
const resultados = [];
const procesos = [];
let nav;

function log(t) {
  console.log(t);
}

async function paso(nombre, fn) {
  const t0 = Date.now();
  try {
    await fn();
    resultados.push({ nombre, ok: true, ms: Date.now() - t0 });
    log(`  ✔ ${nombre} (${Date.now() - t0} ms)`);
  } catch (e) {
    resultados.push({ nombre, ok: false, ms: Date.now() - t0, error: e.message });
    log(`  ✘ ${nombre}\n      ${e.message}`);
    if (nav) {
      try {
        log(`      captura: ${await nav.captura(SALIDA, 'fallo-' + resultados.length)}`);
      } catch {
        // Sin captura.
      }
    }
    throw e;
  }
}

function afirmar(condicion, mensaje) {
  if (!condicion) throw new Error(mensaje);
}

function ejecutar(nombre, cmd, args, opciones = {}) {
  // npx es un .cmd en Windows: necesita la consola; el resto se ejecuta directo (sin concatenar argumentos).
  const r = cmd === 'npx'
    ? spawnSync(['npx', ...args].join(' '), { cwd: RAIZ, encoding: 'utf8', shell: true, ...opciones })
    : spawnSync(cmd, args, { cwd: RAIZ, encoding: 'utf8', ...opciones });
  writeFileSync(join(SALIDA, `${nombre}.log`), `${r.stdout ?? ''}\n${r.stderr ?? ''}`);
  if (r.status !== 0) throw new Error(`${nombre} terminó con código ${r.status} (ver ${join(SALIDA, nombre + '.log')})`);
  return r.stdout;
}

function iniciar(nombre, dll, env) {
  const salida = createWriteStream(join(SALIDA, `${nombre}.log`));
  const p = spawn('dotnet', [dll], { cwd: dirname(dll), env, stdio: ['ignore', 'pipe', 'pipe'] });
  p.stdout.pipe(salida);
  p.stderr.pipe(salida);
  procesos.push(p);
  return p;
}

async function esperarHttp(url, ms = 60_000) {
  const fin = Date.now() + ms;
  while (Date.now() < fin) {
    try {
      if ((await fetch(url)).ok) return;
    } catch {
      // Todavía arrancando.
    }
    await espera(300);
  }
  throw new Error(`${url} no respondió en ${ms} ms`);
}

/** Consulta en la base temporal con sqlcmd dentro del contenedor (la clave la toma el contenedor, no se muestra). */
function sql(consulta, base = BASE_DATOS) {
  const r = spawnSync(
    'docker',
    ['exec', 'wabulk-sqlserver', 'sh', '-c',
      `/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -I -d ${base} -W -h -1 -s "|" -Q "SET NOCOUNT ON; ${consulta.replace(/"/g, '\\"')}"`],
    { encoding: 'utf8' },
  );
  if (r.status !== 0) throw new Error(`sqlcmd: ${r.stderr || r.stdout}`);
  return r.stdout.trim();
}

function claveSa() {
  const linea = readFileSync(join(RAIZ, '.env'), 'utf8').split(/\r?\n/).find((l) => l.startsWith('SQLSERVER_SA_PASSWORD='));
  if (!linea) throw new Error('No está SQLSERVER_SA_PASSWORD en .env');
  return linea.slice('SQLSERVER_SA_PASSWORD='.length).trim();
}

const claveNueva = () => 'E2e!' + randomBytes(9).toString('base64url') + 'aA1';

// ---------- preparación ----------
const cadena = `Server=127.0.0.1,14333;Database=${BASE_DATOS};User Id=sa;Password=${claveSa()};Encrypt=True;TrustServerCertificate=True`;
if (!/Database=WaBulk_E2E_\d{14};/.test(cadena)) throw new Error('La prueba solo puede usar una base WaBulk_E2E_<id>.');

const meta = { phone: '1524289747428097', waba: '2088365445132575' };
const envComun = {
  ...process.env,
  ASPNETCORE_ENVIRONMENT: 'Development',
  DOTNET_ENVIRONMENT: 'Development',
  ConnectionStrings__WaBulk: cadena,
  Meta__BaseUrl: `http://127.0.0.1:${PUERTOS.meta}/`,
  Meta__AccessToken: 'e2e-token-falso',
  Meta__AppSecret: APP_SECRET,
  Meta__VerifyToken: 'e2e-verify-token',
  Meta__PhoneNumberId: meta.phone,
  Meta__WabaId: meta.waba,
};
const dll = (proyecto) => join(BUILD, 'bin', proyecto, 'debug', `${proyecto}.dll`);
const usuarios = {
  admin: { usuario: 'e2e-admin', clave: claveNueva() },
  operador: { usuario: 'e2e-operador', clave: claveNueva() },
  aprobador: { usuario: 'e2e-aprobador', clave: claveNueva() },
};
const ANA = { wa: '59170000101', nombre: 'Ana E2E' };
let metaFalso;
let web;
let hooks;

async function preparar() {
  log(`\nPrueba E2E ${ID} — base ${BASE_DATOS}, salida ${SALIDA}\n`);
  if (SIN_COMPILAR) {
    for (const p of ['WaBulk.Api', 'WaBulk.Webhooks', 'WaBulk.Worker']) afirmar(existsSync(dll(p)), `Falta compilar: ${dll(p)}`);
  } else {
  await paso('Compilar backend aparte (--artifacts-path, no bloquea la Api de desarrollo)', async () => {
    ejecutar('build-backend', 'dotnet', ['build', 'src/backend/WaBulk.sln', '-c', 'Debug', '--artifacts-path', BUILD, '-nologo', '-v', 'q']);
    for (const p of ['WaBulk.Api', 'WaBulk.Webhooks', 'WaBulk.Worker']) afirmar(existsSync(dll(p)), `No se encontró ${dll(p)}`);
  });
  await paso('Build de producción del frontend (npx ng build)', async () => {
    ejecutar('build-frontend', 'npx', ['ng', 'build'], { cwd: join(RAIZ, 'src/frontend') });
  });
  }
  await paso(`Base temporal ${BASE_DATOS} con todas las migraciones`, async () => {
    ejecutar('migraciones', 'dotnet', ['ef', 'database', 'update', '--project', 'src/backend/WaBulk.Infrastructure',
      '--startup-project', 'src/backend/WaBulk.Api', '--configuration', 'Release'], { env: envComun });
    afirmar(sql("SELECT COUNT(*) FROM sys.tables WHERE name IN ('Campaign','DispatchJob','Contact','Template')") === '4', 'Faltan tablas');
  });
  await paso('Primer Admin con crear-admin (clave por entrada estándar, no queda escrita)', async () => {
    const r = spawnSync('dotnet', [dll('WaBulk.Api'), 'crear-admin', '--usuario', usuarios.admin.usuario], {
      cwd: dirname(dll('WaBulk.Api')), env: envComun, input: usuarios.admin.clave + '\n', encoding: 'utf8',
    });
    afirmar(r.status === 0, `crear-admin: ${r.stderr}`);
  });
  await paso('Levantar Meta falso, Api, receptor, Worker (despachador encendido) y el sitio con proxy', async () => {
    metaFalso = iniciarMetaFalso(PUERTOS.meta);
    iniciar('api', dll('WaBulk.Api'), { ...envComun, ASPNETCORE_URLS: `http://127.0.0.1:${PUERTOS.api}` });
    iniciar('webhooks', dll('WaBulk.Webhooks'), { ...envComun, ASPNETCORE_URLS: `http://127.0.0.1:${PUERTOS.webhooks}` });
    iniciar('worker', dll('WaBulk.Worker'), {
      ...envComun,
      Dispatcher__Enabled: 'true',
      Dispatcher__AllowedFrom: '00:00',
      Dispatcher__AllowedUntil: '23:59',
    });
    web = iniciarServidor({ puerto: PUERTOS.web, carpeta: join(RAIZ, 'src/frontend/dist/frontend/browser'), api: `http://127.0.0.1:${PUERTOS.api}` });
    await esperarHttp(`http://127.0.0.1:${PUERTOS.api}/health`);
    await esperarHttp(`http://127.0.0.1:${PUERTOS.webhooks}/health`);
    hooks = crearWebhooks({ url: `http://127.0.0.1:${PUERTOS.webhooks}/webhooks/whatsapp`, appSecret: APP_SECRET, wabaId: meta.waba, phoneNumberId: meta.phone });
    nav = await Navegador.abrir({ perfil: join(SALIDA, '.perfil'), puerto: PUERTOS.cdp, base: `http://127.0.0.1:${PUERTOS.web}` });
    await nav.ancho(1366);
    await nav.temaSistema(false);
  });
}

// ---------- ayudas de la intranet ----------
async function entrar(u) {
  await nav.ir('/login');
  await nav.escribir('#usuario', u.usuario);
  await nav.escribir('#clave', u.clave);
  await nav.clic('button[type="submit"]');
  await nav.esperar(`location.pathname === '/' && document.body.innerText.includes('Hola, ${u.usuario}')`, { que: `inicio de ${u.usuario}` });
}

async function salir() {
  await nav.clic('.boton-salir');
  await nav.esperar(`location.pathname === '/login'`, { que: 'volver al login' });
}

async function menu(texto) {
  const destino = await nav.eval(
    `Array.from(document.querySelectorAll('nav[aria-label="Menú principal"] a')).find((a) => a.innerText.trim() === ${JSON.stringify(texto)})?.getAttribute('href')`,
  );
  afirmar(destino, `«${texto}» no está en el menú`);
  await nav.clicTexto(texto, { dentro: 'nav[aria-label="Menú principal"]', etiqueta: 'a' });
  // La pantalla está lista cuando cambió la ruta, su componente pintó el título y ya no dice "Cargando…".
  await nav.esperar(
    `location.pathname === ${JSON.stringify(destino)} && !!document.querySelector('main.pagina > :not(router-outlet) h1, main.pagina h1') && !document.querySelector('.cargando') && !document.body.innerText.includes('Hola, ' + (document.querySelector('.usuario-datos strong')?.innerText ?? '-')) || (${JSON.stringify(destino)} === '/' && location.pathname === '/')`,
    { que: `abrir ${texto}` },
  );
  await espera(300); // Angular termina de conectar los campos del formulario (ngModel) en una microtarea posterior.
}

async function avisoContiene(t, ms = 10_000) {
  await nav.esperar(`(document.querySelector('.aviso:not(.error)')?.innerText ?? '').includes(${JSON.stringify(t)})`, { ms, que: `aviso «${t}»` });
}

/** Pulsa "Actualizar" hasta que el texto aparezca (lo que llega por webhook lo procesa el Worker cada 2 s). */
async function actualizarHasta(t, ms = 20_000) {
  const fin = Date.now() + ms;
  while (Date.now() < fin) {
    if ((await nav.texto('main')).includes(t)) return;
    await nav.clicTexto('Actualizar');
    await espera(1000);
  }
  throw new Error(`No apareció «${t}» en ${ms} ms`);
}

const contador = (nombre) =>
  nav.eval(`Number(Array.from(document.querySelectorAll('.contador')).find((c) => c.querySelector('span')?.innerText.trim().toLowerCase() === ${JSON.stringify(nombre.toLowerCase())})?.querySelector('strong')?.innerText ?? -1)`);

// ---------- escenario ----------
async function escenario() {
  log('\nLogin, usuarios y marco');
  await paso('Login con clave mala muestra el error y no entra', async () => {
    await nav.ir('/login');
    await nav.escribir('#usuario', usuarios.admin.usuario);
    await nav.escribir('#clave', 'Mala-clave-1!');
    await nav.clic('button[type="submit"]');
    await nav.esperar(`!!document.querySelector('.login .aviso.error')`, { que: 'error de login' });
    afirmar((await nav.eval('location.pathname')) === '/login', 'No debía entrar');
  });
  await paso('Login del Admin → Inicio con sus accesos directos (solo Usuarios)', async () => {
    await entrar(usuarios.admin);
    const accesos = await nav.eval(`Array.from(document.querySelectorAll('.acceso strong'), (a) => a.innerText)`);
    afirmar(JSON.stringify(accesos) === '["Usuarios"]', `Accesos del Admin: ${accesos}`);
  });
  await paso('Usuarios: error por campo y alta de e2e-operador (Operador) y e2e-aprobador (Aprobador)', async () => {
    await menu('Usuarios');
    await nav.escribir('input[name="usuario"]', '');
    await nav.esperar(`document.body.innerText.includes('Escribe el nombre de usuario.')`, { que: 'error por campo' });
    for (const [u, rol] of [[usuarios.operador, 'Operador'], [usuarios.aprobador, 'Aprobador']]) {
      await nav.escribir('input[name="usuario"]', u.usuario);
      await nav.eval(`Array.from(document.querySelectorAll('.crear label.check')).find((l) => l.innerText.trim() === '${rol}').querySelector('input').click()`);
      await nav.escribir('input[name="clave"]', u.clave);
      await nav.clicTexto('Crear');
      await avisoContiene(`Usuario ${u.usuario} creado`);
      await nav.esperar(
        `document.querySelector('input[name="usuario"]').value === '' && !Array.from(document.querySelectorAll('.crear input[type=checkbox]')).some((c) => c.checked)`,
        { que: 'formulario de usuario vacío tras crear' },
      );
    }
  });
  await paso('Tema: Sistema → Claro → Oscuro, se recuerda al recargar y vuelve a Sistema', async () => {
    afirmar((await nav.eval(`document.documentElement.getAttribute('data-tema')`)) === null, 'Empieza en Sistema');
    await nav.clic('.boton-tema');
    afirmar((await nav.eval(`document.documentElement.getAttribute('data-tema')`)) === 'claro', 'Claro');
    await nav.clic('.boton-tema');
    afirmar((await nav.eval(`document.documentElement.getAttribute('data-tema')`)) === 'oscuro', 'Oscuro');
    await nav.esperar(`getComputedStyle(document.body).backgroundColor === 'rgb(22, 19, 17)'`, { ms: 3000, que: 'fondo oscuro (#161311)' });
    await nav.ir('/usuarios');
    afirmar((await nav.eval(`document.documentElement.getAttribute('data-tema')`)) === 'oscuro', 'Se recuerda tras recargar');
    await nav.clic('.boton-tema');
    afirmar((await nav.eval(`document.documentElement.getAttribute('data-tema')`)) === null, 'Vuelve a Sistema');
  });
  await paso('Teclado: Tab llega primero a "Saltar al contenido" y Enter lleva el foco al contenido', async () => {
    await nav.ir('/usuarios');
    await nav.eval('document.activeElement?.blur(); window.focus(); true');
    await nav.tecla('Tab', 'Tab', 9);
    afirmar((await nav.eval(`document.activeElement?.className`)) === 'saltar', 'El primer Tab no fue al enlace');
    await nav.cmd('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13, text: '\r' });
    await nav.cmd('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13 });
    await nav.esperar(`document.activeElement?.id === 'principal'`, { que: 'foco en el contenido' });
  });
  await paso('Salir y volver a una pantalla protegida → login', async () => {
    await salir();
    await nav.ir('/usuarios');
    await nav.esperar(`location.pathname === '/login'`, { que: 'guardia de sesión' });
  });

  log('\nMensajes recientes y consentimiento');
  await paso('Operador: menú agrupado (Clientes, Envíos) sin Aprobación ni Administración', async () => {
    await entrar(usuarios.operador);
    const grupos = await nav.eval(`Array.from(document.querySelectorAll('.grupo-menu'), (g) => g.innerText.trim())`);
    afirmar(JSON.stringify(grupos) === '["CLIENTES","ENVÍOS"]' || JSON.stringify(grupos) === '["Clientes","Envíos"]', `Grupos: ${grupos}`);
  });
  let wamidOferta;
  await paso('Llega "Hola" por webhook → aparece en Mensajes recientes, con la hora local', async () => {
    await hooks.texto(ANA.wa, ANA.nombre, 'Hola, ¿tienen costilla de res?');
    await menu('Mensajes recientes');
    await actualizarHasta('tienen costilla de res');
    afirmar((await nav.texto('main')).includes(ANA.nombre), 'Falta el nombre del cliente');
    // La Api manda UTC sin "Z": la pantalla debe mostrar la hora local (en Bolivia, 4 h menos que UTC).
    const ahora = await nav.eval(`(() => { const d = new Date(); const p = (n) => String(n).padStart(2, '0'); return p(d.getDate()) + '/' + p(d.getMonth() + 1) + ' ' + p(d.getHours()) + ':'; })()`);
    afirmar((await nav.texto('main table')).includes(ahora), `La hora del mensaje no es la local (se esperaba «${ahora}…»)`);
  });
  await paso('Diálogo: Escape cancela sin llamar a Meta', async () => {
    const antes = metaFalso.enviados().length;
    await nav.clicTexto('Ofrecer promociones');
    await nav.esperar(`document.querySelector('dialog.dialogo')?.open && document.querySelector('dialog.dialogo').innerText.includes('¿Enviar a ${ANA.nombre}')`, { que: 'diálogo abierto' });
    afirmar((await nav.eval(`document.activeElement?.innerText.trim()`)) === 'Cancelar', 'El foco debe empezar en Cancelar');
    await nav.tecla('Escape', 'Escape', 27);
    await nav.esperar(`!document.querySelector('dialog.dialogo')?.open`, { que: 'diálogo cerrado' });
    await espera(500);
    afirmar(metaFalso.enviados().length === antes, 'No debía enviar nada');
  });
  await paso('Ofrecer promociones → «Enviar pregunta» → Meta recibe un solo mensaje interactivo', async () => {
    await nav.clicTexto('Ofrecer promociones');
    await nav.clicTexto('Enviar pregunta', { dentro: 'dialog.dialogo' });
    await nav.esperar(`/enviad/i.test(document.querySelector('.aviso:not(.error)')?.innerText ?? '')`, { que: 'aviso de oferta enviada' });
    const envios = metaFalso.enviados().filter((e) => e.type === 'interactive');
    afirmar(envios.length === 1, `Mensajes interactivos: ${envios.length}`);
    afirmar(String(envios[0].to).replace('+', '') === ANA.wa, `Destino: ${envios[0].to}`);
    afirmar(String(envios[0].biz_opaque_callback_data).startsWith('oferta:'), 'Falta oferta:{Id}');
    wamidOferta = envios[0].wamid;
  });
  await paso('El cliente toca «Sí, quiero ofertas» (webhook) → «Acepta promociones»', async () => {
    await hooks.respuestaBoton(ANA.wa, ANA.nombre, 'OPTIN_PROMOS', 'Sí, quiero ofertas', wamidOferta);
    await actualizarHasta('Acepta promociones');
    await actualizarHasta('Respondió: Sí');
  });

  log('\nListas, importación y contactos');
  await paso('Listas: crear «Clientes E2E»', async () => {
    await menu('Listas');
    await nav.escribir('input[name="nombre"]', 'Clientes E2E');
    await nav.clicTexto('Crear lista');
    await nav.esperar(`document.querySelector('main table')?.innerText.includes('Clientes E2E')`, { que: 'lista creada' });
  });
  await paso('Importar CSV (2 OptIn con evidencia, 1 baja, 1 inválido) a la lista → resumen correcto', async () => {
    const csv = join(SALIDA, 'contactos.csv');
    writeFileSync(
      csv,
      'Telefono,Nombre,ClienteId,Consentimiento,Evidencia\n' +
        '70000102,Carnicería E2E,C-102,OptIn,Formulario firmado en tienda 17/11/2026\n' +
        '70000103,Restaurante E2E,C-103,OptIn,Formulario firmado en tienda 17/11/2026\n' +
        '70000104,Cliente Baja E2E,C-104,OptOut,\n' +
        '12,Teléfono malo,,,\n',
    );
    await menu('Importar');
    await nav.elegir('select[name="lista"]', 'Clientes E2E');
    await nav.elegirArchivo('input[type="file"]', csv);
    await nav.clicTexto('Importar');
    await nav.esperar(`(document.querySelector('.resumen')?.innerText ?? '').replace(/\\s+/g, ' ').includes('4 filas')`, { ms: 15_000, que: 'resumen de importación' });
    const resumen = (await nav.texto('.resumen')).replace(/\s+/g, ' ');
    afirmar(resumen.includes('3 válidas') && resumen.includes('1 con error'), `Resumen: ${resumen}`);
  });
  await paso('Contactos: elegir a Ana, agregarla a la lista y ver su historial (foco en el detalle)', async () => {
    await menu('Contactos');
    await nav.esperar(`document.querySelector('main table')?.innerText.includes('${ANA.nombre}')`, { que: 'Ana en la tabla' });
    await nav.clic(`input[aria-label="Elegir ${ANA.nombre}"]`);
    await nav.elegir('select[name="lista"]', 'Clientes E2E');
    await nav.clicTexto('Agregar a la lista');
    await nav.esperar(`/agregados a "Clientes E2E"/.test(document.querySelector('.aviso:not(.error)')?.innerText ?? '')`, { que: 'aviso agregado' });
    await nav.clic(`button[aria-label="Ver ${ANA.nombre}"]`);
    await nav.esperar(`document.querySelector('.detalle')?.innerText.includes('OptIn')`, { que: 'detalle con OptIn' });
    await nav.esperar(`document.activeElement?.classList.contains('detalle')`, { que: 'foco en el detalle' });
  });

  log('\nPlantillas y campaña con cuatro ojos');
  await paso('Plantillas: «Sincronizar con Meta» trae promo_e2e Aprobada', async () => {
    await menu('Plantillas');
    await nav.clicTexto('Sincronizar con Meta');
    await nav.esperar(`(document.querySelector('main table')?.innerText ?? '').includes('promo_e2e') && document.querySelector('main table').innerText.includes('Aprobada')`, { que: 'promo_e2e Aprobada' });
  });
  await paso('Plantillas: nueva plantilla con vista previa al lado → Meta la recibe y queda En revisión', async () => {
    await nav.clicTexto('Nueva plantilla');
    await nav.escribir('input[name="nombre"]', 'promo_nueva_e2e');
    await nav.escribir('textarea[name="cuerpo"]', 'Hola {{nombre}}, tenemos ofertas de cortes.');
    await nav.escribir({ etiqueta: 'Ejemplo de {{nombre}}' }, 'Ana');
    await nav.esperar(`document.querySelector('.editor-previa')?.innerText.includes('Hola Ana, tenemos ofertas')`, { que: 'vista previa' });
    await nav.clicTexto('Enviar a Meta');
    await nav.esperar(`(document.querySelector('main table')?.innerText ?? '').includes('promo_nueva_e2e')`, { ms: 15_000, que: 'plantilla nueva en la tabla' });
    afirmar(metaFalso.peticiones.some((p) => p.metodo === 'POST' && p.ruta.endsWith('/message_templates') && p.cuerpo?.name === 'promo_nueva_e2e'), 'Meta no recibió la plantilla');
  });
  await paso('Campañas: «Guardar borrador» vacío muestra los errores; luego borrador completo → Enviar a aprobación', async () => {
    await menu('Campañas');
    await nav.clicTexto('Nueva campaña');
    await nav.clicTexto('Guardar borrador');
    await nav.esperar(`document.body.innerText.includes('Escribe el nombre de la campaña.') && document.body.innerText.includes('Elige la plantilla del mensaje.')`, { que: 'errores por campo' });
    await nav.escribir('input[name="nombre"]', 'Promo E2E parrillera');
    await nav.elegir('select[name="plantilla"]', 'promo_e2e');
    await nav.esperar(`document.querySelectorAll('fieldset.parametro').length === 2`, { que: 'parámetros de la plantilla' });
    await nav.escribir({ etiqueta: '{{nombre}}' }, 'cliente');
    await nav.escribir({ etiqueta: '{{promocion}}' }, '15 % en cortes para parrilla');
    await nav.elegir('select[name="lista"]', 'Clientes E2E');
    await nav.clicTexto('Guardar borrador');
    await nav.esperar(`document.querySelector('.detalle')?.innerText.includes('Promo E2E parrillera')`, { ms: 15_000, que: 'detalle de la campaña' });
    await nav.clicTexto('Enviar a aprobación');
    await nav.esperar(`(document.querySelector('.detalle h2')?.innerText ?? '').includes('Por aprobar')`, { que: 'Por aprobar' });
  });
  await paso('Aprobador: no ve Contactos (Sin permiso) y aprueba la versión que revisó', async () => {
    await salir();
    await entrar(usuarios.aprobador);
    await nav.ir('/contactos');
    await nav.esperar(`document.body.innerText.includes('Sin permiso')`, { que: 'Sin permiso' });
    afirmar((await nav.texto('.usuario-datos')).includes('Aprobador') && !(await nav.texto('.usuario-datos')).includes('Operador'), 'El aprobador debe tener solo el rol Aprobador');
    await menu('Aprobaciones');
    await nav.clicTexto('Revisar');
    await nav.esperar(`document.querySelector('.detalle .previa')?.innerText.includes('este sábado tenemos 15 % en cortes')`, { que: 'vista previa con parámetros' });
    await nav.clicTexto('Aprobar versión 1');
    await nav.esperar(`!!document.querySelector('.aviso:not(.error)') && !document.querySelector('.aviso.error')`, { que: 'aviso de aprobación' });
  });

  log('\nDespacho, monitor en vivo y freno de emergencia');
  await paso('Monitor (Operador): conexión en vivo por SignalR y 3 enviados (Ana + 2 importados; la baja queda omitida)', async () => {
    await salir();
    await entrar(usuarios.operador);
    await menu('Monitor');
    await nav.esperar(`document.querySelector('.vivo')?.classList.contains('activo')`, { ms: 15_000, que: 'En vivo' });
    await nav.eval('window.__sinRecargar = true');
    await nav.esperar(`(${contadorJs('Enviados')}) === 3`, { ms: 40_000, que: 'Enviados = 3' });
    afirmar((await contador('Omitidos')) >= 1, 'Debe haber al menos un omitido (la baja importada)');
    const plantillas = metaFalso.enviados().filter((e) => e.type === 'template');
    afirmar(plantillas.length === 3, `Meta recibió ${plantillas.length} plantillas`);
  });
  await paso('Webhooks delivered ×3 y read ×1 → los contadores cambian sin recargar en menos de 5 s', async () => {
    const plantillas = metaFalso.enviados().filter((e) => e.type === 'template');
    const t0 = Date.now();
    for (const e of plantillas) await hooks.estado(e.wamid, 'delivered', e.to, e.biz_opaque_callback_data);
    await hooks.estado(plantillas[0].wamid, 'read', plantillas[0].to, plantillas[0].biz_opaque_callback_data);
    await nav.esperar(`(${contadorJs('Entregados')}) === 3 && (${contadorJs('Leídos')}) === 1`, { ms: 10_000, que: 'Entregados 3 y Leídos 1' });
    const ms = Date.now() - t0;
    afirmar(await nav.eval('window.__sinRecargar === true'), 'La página se recargó');
    afirmar(ms < 5000 + 2500, `Tardó ${ms} ms (meta: < 5 s más el procesamiento del Worker)`);
    log(`      (webhook → contador en pantalla: ${ms} ms)`);
  });
  await paso('Freno: plantilla PAUSED por webhook → banner EN PAUSA en el Monitor sin recargar', async () => {
    await hooks.plantillaPausada(PLANTILLA_E2E.id, PLANTILLA_E2E.name, PLANTILLA_E2E.language);
    await nav.esperar(`document.querySelector('.banner-pausa')?.innerText.includes('EN PAUSA')`, { ms: 15_000, que: 'banner EN PAUSA' });
    afirmar(await nav.eval('window.__sinRecargar === true'), 'La página se recargó');
    await nav.captura(SALIDA, 'monitor-en-pausa');
  });
  await paso('Aprobador reanuda desde Alertas → «Despacho activo»', async () => {
    await salir();
    await entrar(usuarios.aprobador);
    await menu('Alertas');
    await nav.esperar(`document.querySelector('.banner-pausa')?.innerText.includes('EN PAUSA')`, { que: 'banner en Alertas' });
    await nav.clicTexto('Revisé el problema: reanudar el despacho');
    await nav.esperar(`document.body.innerText.includes('Despacho activo')`, { que: 'Despacho activo' });
  });
  await paso('Operador registra una baja manual de Ana → aparece en Bajas (capa Empresa)', async () => {
    await salir();
    await entrar(usuarios.operador);
    await menu('Contactos');
    await nav.clic(`button[aria-label="Ver ${ANA.nombre}"]`);
    await nav.escribir('input[name="motivo"]', 'Pidió la baja por teléfono (E2E)');
    await nav.clicTexto('Registrar baja');
    await avisoContiene('Baja registrada');
    await menu('Bajas');
    await nav.esperar(`document.querySelector('main table')?.innerText.includes('${ANA.nombre}') && document.querySelector('main table').innerText.includes('Empresa')`, { que: 'Ana en Bajas' });
  });
  await paso('Envíos inciertos: la pantalla carga (sin inciertos en esta corrida)', async () => {
    await menu('Envíos inciertos');
    afirmar((await nav.texto('main')).includes('No hay envíos inciertos'), 'Debía estar vacía');
  });

  log('\nMóvil (390 px)');
  await paso('Cada pantalla del Operador sin desborde horizontal; menú se abre con «Menú» y se cierra con Escape', async () => {
    await nav.ancho(390);
    for (const ruta of ['/', '/mensajes', '/contactos', '/importar', '/listas', '/plantillas', '/campanias', '/monitor', '/revision', '/bajas']) {
      await nav.ir(ruta);
      await nav.esperar(`!document.querySelector('.cargando')`, { que: `cargar ${ruta}` });
      const ancho = await nav.eval('document.documentElement.scrollWidth');
      afirmar(ancho <= 390, `${ruta}: scrollWidth ${ancho}`);
    }
    await nav.ir('/mensajes');
    await nav.clic('.boton-menu');
    await nav.esperar(`document.querySelector('.lateral')?.classList.contains('abierto')`, { que: 'menú abierto' });
    await nav.tecla('Escape', 'Escape', 27);
    await nav.esperar(`!document.querySelector('.lateral')?.classList.contains('abierto')`, { que: 'menú cerrado' });
    await nav.captura(SALIDA, 'movil-mensajes');
    await nav.ancho(1366);
  });

  log('\nAdministración');
  await paso('Admin desactiva a e2e-aprobador con el diálogo (botón rojo) → Desactivado', async () => {
    await salir();
    await entrar(usuarios.admin);
    await menu('Usuarios');
    await nav.eval(`Array.from(document.querySelectorAll('tbody tr')).find((tr) => tr.innerText.includes('e2e-aprobador')).querySelector('button.peligro').click()`);
    await nav.clicTexto('Desactivar', { dentro: 'dialog.dialogo' });
    await nav.esperar(`Array.from(document.querySelectorAll('tbody tr')).find((tr) => tr.innerText.includes('e2e-aprobador'))?.innerText.includes('Desactivado')`, { que: 'Desactivado' });
  });
  await paso('Sin errores de JavaScript ni respuestas 5xx durante todo el recorrido', async () => {
    afirmar(nav.errores.length === 0, `Errores en la consola:\n      ${nav.errores.join('\n      ')}`);
    afirmar(nav.respuestas5xx.length === 0, `Respuestas 5xx:\n      ${nav.respuestas5xx.join('\n      ')}`);
  });

  log('\nBase de datos (lo que dejó la intranet)');
  await paso('Puerta "cero duplicados": ningún (versión, contacto) con dos intentos Accepted', async () => {
    const r = sql(`SELECT COUNT(*) FROM (SELECT j.CampaignVersionId, j.ContactId FROM SendAttempt a JOIN DispatchJob j ON j.Id = a.DispatchJobId WHERE a.Estado = 'Accepted' AND j.CampaignVersionId IS NOT NULL GROUP BY j.CampaignVersionId, j.ContactId HAVING COUNT(*) > 1) d`);
    afirmar(r === '0', `Duplicados: ${r}`);
  });
  await paso('AuditLog con cada acción hecha desde la intranet y su usuario', async () => {
    const filas = sql(`SELECT DISTINCT Accion + '|' + Usuario FROM AuditLog`).split(/\r?\n/).map((l) => l.trim());
    const esperadas = [
      'CrearUsuario|e2e-admin', 'OfrecerPromociones|e2e-operador', 'CrearLista|e2e-operador', 'ImportarContactos|e2e-operador',
      'AgregarALista|e2e-operador', 'SincronizarPlantillas|e2e-operador', 'CrearPlantilla|e2e-operador', 'CrearCampana|e2e-operador',
      'EnviarAprobacionCampana|e2e-operador', 'AprobarCampana|e2e-aprobador', 'ReanudarDespacho|e2e-aprobador',
      'BajaManual|e2e-operador', 'DesactivarUsuario|e2e-admin',
    ];
    const faltan = esperadas.filter((e) => !filas.includes(e));
    afirmar(faltan.length === 0, `Faltan en AuditLog: ${faltan.join(', ')} (hay: ${filas.join(', ')})`);
  });
}

function contadorJs(nombre) {
  return `Number(Array.from(document.querySelectorAll('.contador')).find((c) => c.querySelector('span')?.innerText.trim().toLowerCase() === ${JSON.stringify(nombre.toLowerCase())})?.querySelector('strong')?.innerText ?? -1)`;
}

// ---------- cierre ----------
async function limpiar() {
  if (MANTENER) {
    log(`\n--mantener: la base ${BASE_DATOS} y los procesos siguen. Sitio: http://127.0.0.1:${PUERTOS.web} — Ctrl+C para terminar.`);
    log(`Usuarios: ${Object.values(usuarios).map((u) => u.usuario).join(', ')} (claves en ${join(SALIDA, 'usuarios.json')})`);
    writeFileSync(join(SALIDA, 'usuarios.json'), JSON.stringify(usuarios, null, 2));
    return;
  }
  await nav?.cerrar();
  for (const p of procesos) p.kill();
  await metaFalso?.cerrar();
  await web?.cerrar();
  await espera(1500);
  try {
    sql(`ALTER DATABASE [${BASE_DATOS}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [${BASE_DATOS}];`, 'master');
    log(`\nBase ${BASE_DATOS} borrada.`);
  } catch (e) {
    log(`\nNo se pudo borrar ${BASE_DATOS}: ${e.message}`);
  }
}

let codigo = 0;
try {
  await preparar();
  await escenario();
} catch {
  codigo = 1;
} finally {
  await limpiar();
  const ok = resultados.filter((r) => r.ok).length;
  const informe = { id: ID, base: BASE_DATOS, ok, total: resultados.length, resultados };
  writeFileSync(join(SALIDA, 'informe.json'), JSON.stringify(informe, null, 2));
  log(`\nResultado: ${ok}/${resultados.length} pasos OK${codigo ? ' — FALLÓ' : ''}. Informe y capturas: ${SALIDA}`);
  if (!MANTENER) process.exit(codigo);
}
