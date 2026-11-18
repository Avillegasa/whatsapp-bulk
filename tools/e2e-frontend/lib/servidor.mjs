// Sirve el build de producción de Angular (dist/frontend/browser) y reenvía /api a la Api, como lo hará nginx en
// producción (mismo dominio, D-029): peticiones normales y la actualización a websocket de SignalR.
import { createServer, request } from 'node:http';
import { connect } from 'node:net';
import { existsSync, readFileSync, statSync } from 'node:fs';
import { extname, join, normalize } from 'node:path';

const TIPOS = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.ico': 'image/x-icon',
  '.svg': 'image/svg+xml',
  '.json': 'application/json',
  '.txt': 'text/plain; charset=utf-8',
};

export function iniciarServidor({ puerto, carpeta, api }) {
  const destino = new URL(api);
  const servidor = createServer((req, res) => {
    if (req.url.startsWith('/api/')) {
      const p = request(
        { host: destino.hostname, port: destino.port, method: req.method, path: req.url, headers: req.headers },
        (r) => {
          res.writeHead(r.statusCode, r.headers);
          r.pipe(res);
        },
      );
      p.on('error', () => {
        res.writeHead(502);
        res.end('Api no disponible');
      });
      req.pipe(p);
      return;
    }
    const ruta = normalize(decodeURIComponent(req.url.split('?')[0])).replace(/^([/\\])+/, '');
    let archivo = join(carpeta, ruta);
    if (!archivo.startsWith(carpeta) || !existsSync(archivo) || statSync(archivo).isDirectory()) {
      archivo = join(carpeta, 'index.html'); // Rutas de Angular (/monitor, /campanias…).
    }
    res.writeHead(200, { 'Content-Type': TIPOS[extname(archivo)] ?? 'application/octet-stream' });
    res.end(readFileSync(archivo));
  });

  // SignalR: el navegador pide "Upgrade: websocket"; se abre un socket a la Api y se copian los bytes en ambos sentidos.
  servidor.on('upgrade', (req, socket, cabeza) => {
    if (!req.url.startsWith('/api/')) return socket.destroy();
    const api = connect(Number(destino.port), destino.hostname, () => {
      const lineas = [`${req.method} ${req.url} HTTP/1.1`];
      for (let i = 0; i < req.rawHeaders.length; i += 2) lineas.push(`${req.rawHeaders[i]}: ${req.rawHeaders[i + 1]}`);
      api.write(lineas.join('\r\n') + '\r\n\r\n');
      if (cabeza?.length) api.write(cabeza);
      api.pipe(socket);
      socket.pipe(api);
    });
    api.on('error', () => socket.destroy());
    socket.on('error', () => api.destroy());
  });

  servidor.listen(puerto, '127.0.0.1');
  return { cerrar: () => new Promise((r) => servidor.close(r)) };
}
