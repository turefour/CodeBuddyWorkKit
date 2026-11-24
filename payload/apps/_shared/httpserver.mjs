/**
 * @module httpserver
 * @category system
 * @purpose Zero-dependency HTTP layer distilled from the three local dashboards: routes, JSON, static, CSRF guard, SSE.
 * @input  createApp({ root, token, tokenHeader })
 * @usage  const app = createApp({ root: process.cwd() }); app.get('/api/health', ...); app.listen(47910);
 * @reuse  yes
 */

import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';

const DEFAULT_MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.ico': 'image/x-icon',
  '.md': 'text/plain; charset=utf-8'
};

const WRITE_METHODS = new Set(['POST', 'PUT', 'PATCH', 'DELETE']);
const LOOPBACK = /^https?:\/\/(127\.0\.0\.1|localhost|\[::1\])(:\d+)?$/i;
/* 连接来源地址：IPv4 / IPv6 / IPv4-mapped 三种回环写法 */
const LOOPBACK_ADDR = /^(127\.|::1$|::ffff:127\.)/;

export function sendJson(res, code, payload) {
  const body = Buffer.from(JSON.stringify(payload), 'utf8');
  res.writeHead(code, {
    'content-type': 'application/json; charset=utf-8',
    'content-length': body.length,
    'cache-control': 'no-store'
  });
  res.end(body);
}

export function sendText(res, code, text, type = 'text/plain; charset=utf-8') {
  const body = Buffer.from(String(text), 'utf8');
  res.writeHead(code, { 'content-type': type, 'content-length': body.length });
  res.end(body);
}

export function readBody(req, limit = 1024 * 1024) {
  return new Promise((resolve) => {
    let raw = '';
    req.on('data', (chunk) => {
      if (raw.length > limit) {
        req.destroy();
        resolve(null);
        return;
      }
      raw += chunk;
    });
    req.on('end', () => {
      if (!raw) return resolve({});
      try {
        resolve(JSON.parse(raw));
      } catch {
        resolve(null);
      }
    });
    req.on('error', () => resolve(null));
  });
}

/**
 * Same-origin JSON check for state-changing requests.
 * @returns {string} '' when allowed, otherwise the reason
 */
export function guardWrite(req, options = {}) {
  const method = String(req.method || 'GET').toUpperCase();
  if (!WRITE_METHODS.has(method)) return '';

  // 1) 连接必须来自本机。
  //    即使开了 lan 把页面暴露到局域网，也不允许远程写 ——
  //    否则同网段任何人拿到 URL 就能调 /api/launch 之类的动作端点。
  const remote = String((req.socket && req.socket.remoteAddress) || '');
  if (remote && !LOOPBACK_ADDR.test(remote)) return 'remote write not allowed';

  // 2) 带了 Origin 就必须是本机页面（防 CSRF）
  const origin = String(req.headers.origin || '');
  if (origin) {
    const allowHost = options.allowHost || '';
    if (allowHost) {
      try {
        if (new URL(origin).host !== allowHost) return 'cross-origin request blocked';
      } catch {
        return 'bad origin';
      }
    } else if (!LOOPBACK.test(origin)) {
      return 'origin not allowed';
    }
  }
  if (String(req.headers['sec-fetch-site'] || '').toLowerCase() === 'cross-site') {
    return 'cross-site request blocked';
  }
  // 3) 带请求体的必须是 JSON（空体 POST 无需 content-type，
  //    否则 /api/hit 这种「只打个标记」的端点会被误拒）
  const hasBody =
    Number(req.headers['content-length'] || 0) > 0 || Boolean(req.headers['transfer-encoding']);
  const ctype = String(req.headers['content-type'] || '').toLowerCase();
  if (hasBody && ctype.indexOf('application/json') !== 0) {
    return 'content-type must be application/json';
  }
  return '';
}

/**
 * 把一个请求路径映射到 root 下的静态文件并写回响应。
 *
 * 从 createApp 里提出来单独导出：已有服务（Pulse）自带路由表，
 * 但静态文件这一层没有理由各写一份 —— 路径穿越校验尤其不该有四个版本。
 *
 * @returns {boolean} true=已处理（含 403/200），false=文件不存在（交给调用方 404）
 */
export function serveStaticFile(options) {
  const root = path.resolve(options.root);
  const pathname = options.pathname || '/';
  const req = options.req;
  const res = options.res;
  const mime = options.mime || DEFAULT_MIME;

  const rel = pathname === '/' ? 'index.html' : pathname.replace(/^\/+/, '');
  const file = path.resolve(root, rel);
  const inside = path.relative(root, file);
  if (!inside || inside.startsWith('..') || path.isAbsolute(inside)) {
    sendText(res, 403, 'forbidden');
    return true;
  }

  let data;
  try {
    const stat = fs.statSync(file);
    if (!stat.isFile()) throw new Error('not a file');
    data = fs.readFileSync(file);
  } catch {
    return false;
  }

  res.writeHead(200, {
    'content-type': mime[path.extname(file).toLowerCase()] || 'application/octet-stream',
    'content-length': data.length,
    'cache-control': 'no-cache'
  });
  res.end(req.method === 'HEAD' ? undefined : data);
  return true;
}

/**
 * Build a tiny app.
 *
 * @param {{ root?: string, token?: string, tokenHeader?: string, mime?: Record<string,string>, log?: (line: string) => void }} [options]
 */
export function createApp(options = {}) {
  const root = path.resolve(options.root || process.cwd());
  const token = options.token || '';
  const tokenHeader = (options.tokenHeader || 'x-app-token').toLowerCase();
  // 额外放行规则：服务自己判断（比如「带了合法 API token」），
  // 用来在标准守卫之外开一个受控的例外，而不是把 guardWrite 整段复制一遍。
  const guardAllow = typeof options.guardAllow === 'function' ? options.guardAllow : null;
  const mime = { ...DEFAULT_MIME, ...(options.mime || {}) };
  const log = options.log || (() => {});
  const routes = [];
  const sseClients = new Set();
  let startedAt = Date.now();

  function compile(pattern) {
    const names = [];
    const source = pattern
      .replace(/[.*+?^${}()|[\]\\]/g, (ch) => (ch === '*' ? '(.*)' : '\\' + ch))
      .replace(/:([A-Za-z_]\w*)/g, (_, name) => {
        names.push(name);
        return '([^/]+)';
      });
    return { regex: new RegExp('^' + source + '$'), names };
  }

  function match(method, pathname) {
    for (const route of routes) {
      if (route.method !== method) continue;
      const hit = route.matcher.regex.exec(pathname);
      if (!hit) continue;
      const params = {};
      route.matcher.names.forEach((name, index) => {
        params[name] = decodeURIComponent(hit[index + 1]);
      });
      return { route, params };
    }
    return null;
  }

  function isWriteMethod(method) {
    return WRITE_METHODS.has(method);
  }

  function authorize(req, pathname) {
    if (token) {
      const given = String(req.headers[tokenHeader] || '');
      const exempt = req.method === 'GET' && pathname === '/api/health';
      if (!exempt && given !== token) return 'token required';
    }
    return '';
  }

  function serveStatic(req, res, pathname) {
    return serveStaticFile({ root, pathname, req, res, mime });
  }

  async function handle(req, res) {
    const started = Date.now();
    const url = new URL(req.url, 'http://127.0.0.1');
    let pathname;
    try {
      pathname = decodeURIComponent(url.pathname);
    } catch {
      pathname = url.pathname;
    }
    const method = String(req.method || 'GET').toUpperCase();

    const denied = authorize(req, pathname);
    if (denied) return sendJson(res, 401, { ok: false, error: denied });

    const found = match(method, pathname);
    if (found) {
      if (isWriteMethod(method)) {
        const bypass = guardAllow ? guardAllow(req) : false;
        if (!bypass) {
          const blocked = guardWrite(req, { allowHost: req.headers.host || '' });
          if (blocked) {
            // 被拦的写请求要留痕：这是安全事件，不该静默 403
            log('write blocked: ' + blocked + ' ' + method + ' ' + pathname);
            return sendJson(res, 403, { ok: false, error: blocked });
          }
        }
      }
      const ctx = {
        req,
        res,
        url,
        params: found.params,
        query: url.searchParams,
        json: (code, payload) => sendJson(res, code, payload),
        text: (code, body, type) => sendText(res, code, body, type),
        body: (limit) => readBody(req, limit)
      };
      await found.route.handler(ctx);
      return undefined;
    }

    if (method === 'GET' || method === 'HEAD') {
      if (serveStatic(req, res, pathname)) return undefined;
      return sendJson(res, 404, { ok: false, error: 'not found' });
    }

    // Path exists for another method? Report 405 instead of a confusing 404.
    const other = routes.some((route) => route.matcher.regex.test(pathname));
    if (other) return sendJson(res, 405, { ok: false, error: 'method not allowed' });
    return sendJson(res, 404, { ok: false, error: 'not found' });
  }

  const app = {
    get(pattern, handler) {
      routes.push({ method: 'GET', pattern, matcher: compile(pattern), handler });
      return app;
    },
    post(pattern, handler) {
      routes.push({ method: 'POST', pattern, matcher: compile(pattern), handler });
      return app;
    },
    put(pattern, handler) {
      routes.push({ method: 'PUT', pattern, matcher: compile(pattern), handler });
      return app;
    },
    delete(pattern, handler) {
      routes.push({ method: 'DELETE', pattern, matcher: compile(pattern), handler });
      return app;
    },
    /** Register an SSE endpoint; returns a broadcaster. */
    sse(pattern) {
      const clients = new Set();
      const app2 = app;
      app2.get(pattern, async (ctx) => {
        ctx.res.writeHead(200, {
          'content-type': 'text/event-stream; charset=utf-8',
          'cache-control': 'no-cache, no-transform',
          connection: 'keep-alive'
        });
        ctx.res.write(': connected\n\n');
        clients.add(ctx.res);
        sseClients.add(ctx.res);
        ctx.req.on('close', () => {
          clients.delete(ctx.res);
          sseClients.delete(ctx.res);
        });
      });
      return {
        send(event, data) {
          const payload = `event: ${event}\ndata: ${JSON.stringify(data)}\n\n`;
          for (const res of clients) {
            try {
              res.write(payload);
            } catch {
              clients.delete(res);
            }
          }
        },
        size: () => clients.size
      };
    },
    listen(port = 0, host = '127.0.0.1') {
      const server = http.createServer((req, res) => {
        const started = Date.now();
        handle(req, res)
          .catch((err) => {
            log('handler failed: ' + err.message);
            try {
              sendJson(res, 500, { ok: false, error: err.message });
            } catch {
              /* response already closed */
            }
          })
          .finally(() => {
            const cost = Date.now() - started;
            if (cost > 1500) log(`slow request ${req.method} ${req.url} ${cost}ms`);
          });
      });
      server.on('close', () => {
        for (const res of sseClients) {
          try {
            res.end();
          } catch {
            /* already gone */
          }
        }
        sseClients.clear();
      });
      server.listen(port, host);
      startedAt = Date.now();
      return server;
    },
    guardWrite,
    readBody,
    sendJson,
    sendText,
    stats: () => ({ routes: routes.length, sse: sseClients.size, uptimeMs: Date.now() - startedAt })
  };

  return app;
}
