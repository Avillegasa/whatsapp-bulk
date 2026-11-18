// Meta falso para la prueba de punta a punta: la Api y el Worker apuntan aquí con Meta:BaseUrl, así ningún mensaje
// sale a WhatsApp. Responde con las mismas formas que la Cloud API (send-messages, message_templates y
// whatsapp_business_manager_messaging_limit) y guarda cada petición para que la prueba la revise.
import { createServer } from 'node:http';

/** Plantilla de marketing aprobada que "existe" en la WABA falsa (la trae "Sincronizar con Meta"). */
export const PLANTILLA_E2E = {
  id: '7770001',
  name: 'promo_e2e',
  language: 'es',
  status: 'APPROVED',
  category: 'MARKETING',
  quality_score: { score: 'GREEN' },
  parameter_format: 'NAMED',
  components: [
    { type: 'HEADER', format: 'TEXT', text: 'Fin de semana parrillero' },
    {
      type: 'BODY',
      text: 'Hola {{nombre}}, este sábado tenemos {{promocion}}.',
      example: { body_text_named_params: [{ param_name: 'nombre', example: 'Ana' }, { param_name: 'promocion', example: '15 % en cortes' }] },
    },
    { type: 'FOOTER', text: 'Responde BAJA para no recibir más' },
    { type: 'BUTTONS', buttons: [{ type: 'QUICK_REPLY', text: 'Me interesa' }, { type: 'QUICK_REPLY', text: 'No recibir más' }] },
  ],
};

export function iniciarMetaFalso(puerto) {
  const peticiones = [];
  let n = 0; // mensajes (wamid)
  let t = 0; // plantillas creadas
  const servidor = createServer((req, res) => {
    let cuerpo = '';
    req.on('data', (c) => (cuerpo += c));
    req.on('end', () => {
      const url = new URL(req.url, 'http://x');
      const json = cuerpo ? safeJson(cuerpo) : null;
      peticiones.push({ metodo: req.method, ruta: url.pathname, query: url.search, cuerpo: json, auth: req.headers.authorization });
      const responder = (status, obj) => {
        res.writeHead(status, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify(obj));
      };
      const partes = url.pathname.split('/').filter(Boolean); // [version, id, recurso?]
      if (req.method === 'POST' && partes[2] === 'messages') {
        n++;
        const wamid = `wamid.E2E.${String(n).padStart(4, '0')}`;
        return responder(200, { messaging_product: 'whatsapp', contacts: [{ input: json?.to, wa_id: json?.to }], messages: [{ id: wamid }] });
      }
      if (req.method === 'GET' && partes[2] === 'message_templates') {
        return responder(200, { data: [PLANTILLA_E2E], paging: { cursors: { before: 'a', after: 'b' } } });
      }
      if (req.method === 'POST' && partes[2] === 'message_templates') {
        return responder(200, { id: String(7770100 + ++t), status: 'PENDING', category: 'MARKETING' });
      }
      if (req.method === 'GET' && partes.length === 2 && url.searchParams.get('fields')?.includes('messaging_limit')) {
        return responder(200, { whatsapp_business_manager_messaging_limit: 'TIER_250', id: partes[1] });
      }
      return responder(404, { error: { message: `Meta falso: ruta no simulada ${req.method} ${url.pathname}`, type: 'OAuthException', code: 100 } });
    });
  });
  servidor.listen(puerto, '127.0.0.1');
  return {
    peticiones,
    /** Mensajes enviados (POST /messages) con su wamid, en orden. */
    enviados: () =>
      peticiones
        .filter((p) => p.metodo === 'POST' && p.ruta.endsWith('/messages'))
        .map((p, i) => ({ ...p.cuerpo, wamid: `wamid.E2E.${String(i + 1).padStart(4, '0')}` })),
    cerrar: () => new Promise((r) => servidor.close(r)),
  };
}

function safeJson(t) {
  try {
    return JSON.parse(t);
  } catch {
    return t;
  }
}
