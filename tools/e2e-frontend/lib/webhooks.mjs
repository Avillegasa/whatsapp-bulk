// Webhooks de Meta firmados (X-Hub-Signature-256 = HMAC-SHA256 del cuerpo con el App Secret), con las mismas formas
// que tools/webhook-replay/payloads: mensaje entrante, respuesta a botón, estados y plantilla pausada.
import { createHmac } from 'node:crypto';

export function crearWebhooks({ url, appSecret, wabaId, phoneNumberId, displayPhone = '15551892694' }) {
  const ts = () => String(Math.floor(Date.now() / 1000));
  // La plantilla aplica un webhook solo si entry.time ≥ su EstadoUtc (D-033): +2 s evita empatar con la sincronización.
  const sobre = (field, value, adelanto = 0) => ({
    object: 'whatsapp_business_account',
    entry: [{ id: wabaId, time: Math.floor(Date.now() / 1000) + adelanto, changes: [{ value, field }] }],
  });
  const mensajes = (waId, nombre, mensaje) =>
    sobre('messages', {
      messaging_product: 'whatsapp',
      metadata: { display_phone_number: displayPhone, phone_number_id: phoneNumberId },
      contacts: [{ profile: { name: nombre }, wa_id: waId }],
      messages: [{ from: waId, timestamp: ts(), ...mensaje }],
    });

  async function enviar(cuerpo) {
    const texto = JSON.stringify(cuerpo);
    const firma = 'sha256=' + createHmac('sha256', appSecret).update(texto).digest('hex');
    const r = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Hub-Signature-256': firma }, body: texto });
    if (r.status !== 200) throw new Error(`El receptor respondió ${r.status} a un webhook firmado`);
  }

  let n = 0;
  const idEntrante = () => `wamid.E2E.IN.${++n}.${Date.now()}`;

  return {
    texto: (waId, nombre, body) => enviar(mensajes(waId, nombre, { id: idEntrante(), type: 'text', text: { body } })),
    respuestaBoton: (waId, nombre, botonId, titulo, contextoWamid) =>
      enviar(
        mensajes(waId, nombre, {
          id: idEntrante(),
          type: 'interactive',
          context: { from: displayPhone, id: contextoWamid },
          interactive: { type: 'button_reply', button_reply: { id: botonId, title: titulo } },
        }),
      ),
    estado: (wamid, estado, waId, attemptId) =>
      enviar(
        sobre('messages', {
          messaging_product: 'whatsapp',
          metadata: { display_phone_number: displayPhone, phone_number_id: phoneNumberId },
          statuses: [{ id: wamid, status: estado, timestamp: ts(), recipient_id: waId, biz_opaque_callback_data: attemptId }],
        }),
      ),
    plantillaPausada: (id, nombre, idioma) =>
      enviar(
        sobre('message_template_status_update', {
          event: 'PAUSED',
          message_template_id: Number(id),
          message_template_name: nombre,
          message_template_language: idioma,
          reason: 'NONE',
          message_template_category: 'MARKETING',
          other_info: { title: 'FIRST_PAUSE', description: 'Plantilla pausada (prueba E2E)' },
        }, 2),
      ),
  };
}
