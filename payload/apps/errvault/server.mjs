#!/usr/bin/env node
/**
 * errvault — CodeBuddy 报错库（HTTP 层）
 *
 * @name errvault
 * @category system
 * @purpose 记录 CodeBuddy 运行中的报错 / 卡死 / 被拦截事件，附根因、解决方案和最小复现代码，
 *          供下次遇到同类问题时优先检索，避免重复试错。
 * @input  --port N（默认 47911）
 * @usage  node server.mjs --port 47911
 * @reuse  yes
 *
 * 路由
 *   GET  /                       索引页
 *   GET  /api/health             存活探针
 *   GET  /api/stats              统计（按 kind / scope / repro / severity / tag 汇总）
 *   GET  /api/logs?tail=N        运行日志尾部
 *   GET  /api/search?q=&kind=&scope=&repro=&severity=&tag=&tool=&limit=
 *   GET  /api/entry?id=
 *   GET  /api/snippet?id=&name=
 *   POST /api/entry              新增或更新（id 存在则合并）
 *   POST /api/snippet            单独上传代码片段
 *   POST /api/hit?id=            记一次「被检索命中并采用」
 *
 * HTTP 骨架复用 apps/_shared/httpserver.mjs：
 *   路由、JSON/文本响应、静态文件、写请求守卫（本机来源 + Origin + JSON）都在那里，
 *   服务自己只关心业务。之前每个服务各写一份，加一层保护要改四处。
 *
 * 安全：只绑 127.0.0.1；写请求由共享守卫拦截；片段名走白名单。
 */

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { createApp } from '../_shared/httpserver.mjs';
import {
  LOG_DIR,
  MAX_SNIPPET,
  readIndex,
  readSnippet,
  saveSnippet,
  search,
  stats,
  upsertEntry,
  bumpHit,
  logLine,
  SAFE_NAME
} from './lib/store.mjs';

// 本文件就在应用根目录（apps/errvault/server.mjs），不需要再往上跳一级
const APP_ROOT = path.dirname(fileURLToPath(import.meta.url));
const WEB_DIR = path.join(APP_ROOT, 'web');

const HOST = process.env.ERRVAULT_HOST || '127.0.0.1';
let PORT = Number(process.env.ERRVAULT_PORT || 47911);

/* 请求体上限：条目正文 + 一个片段，512 KB 足够，也挡住误传的大文件 */
const MAX_BODY = 512 * 1024;

/* ---------------------------------------------------------------- 日志 */

/* 查询日志节流：同一个关键词 10 秒内只记一行。
   前端搜索框是 250ms 防抖，不节流的话打字期间能刷出几十行。 */
const _lastSearchLog = new Map();

function logSearch(q, hits) {
  const key = String(q || '').trim();
  if (!key) return; // 空查询是列表模式，不记
  const now = Date.now();
  if (now - (_lastSearchLog.get(key) || 0) < 10000) return;
  _lastSearchLog.set(key, now);
  if (_lastSearchLog.size > 200) _lastSearchLog.clear();
  logLine('info', 'search', 'q=' + JSON.stringify(key).slice(0, 120) + '  hits=' + hits);
}

/* ---------------------------------------------------------------- 应用 */

const app = createApp({ root: WEB_DIR });

app.get('/api/health', (ctx) => {
  const idx = readIndex();
  ctx.json(200, {
    ok: true,
    app: 'errvault',
    pid: process.pid,
    port: PORT,
    entries: idx.entries.length
  });
});

app.get('/api/stats', (ctx) => {
  ctx.json(200, { ok: true, ...stats() });
});

// 运行日志：只读当月文件尾部，供页面「日志」页签展示
app.get('/api/logs', (ctx) => {
  const tail = Math.min(Math.max(Number(ctx.query.get('tail')) || 200, 1), 2000);
  const d = new Date();
  const ym = d.getFullYear() + String(d.getMonth() + 1).padStart(2, '0');
  const file = path.join(LOG_DIR, 'errvault-' + ym + '.log');
  let raw = '';
  try {
    raw = fs.readFileSync(file, 'utf8');
  } catch {
    raw = '';
  }
  const all = raw ? raw.replace(/\r/g, '').trimEnd().split('\n') : [];
  ctx.json(200, {
    ok: true,
    file: path.basename(file),
    size: Buffer.byteLength(raw, 'utf8'),
    total: all.length,
    lines: all.slice(-tail)
  });
});

app.get('/api/search', (ctx) => {
  const q = ctx.query.get('q') || '';
  const rows = search(readIndex(), {
    q,
    kind: ctx.query.get('kind'),
    tool: ctx.query.get('tool'),
    tag: ctx.query.get('tag'),
    scope: ctx.query.get('scope'),
    repro: ctx.query.get('repro'),
    severity: ctx.query.get('severity'),
    limit: ctx.query.get('limit')
  });
  logSearch(q, rows.length);
  ctx.json(200, { ok: true, count: rows.length, rows });
});

app.get('/api/entry', (ctx) => {
  const id = ctx.query.get('id') || '';
  const entry = readIndex().entries.find((x) => x.id === id);
  if (!entry) return ctx.json(404, { ok: false, error: 'entry not found' });
  return ctx.json(200, { ok: true, entry });
});

app.get('/api/snippet', (ctx) => {
  const body = readSnippet(ctx.query.get('id') || '', ctx.query.get('name') || '');
  if (body == null) return ctx.json(404, { ok: false, error: 'snippet not found' });
  return ctx.text(200, body, 'text/plain; charset=utf-8');
});

app.post('/api/entry', async (ctx) => {
  const body = await ctx.body(MAX_BODY);
  if (!body || typeof body !== 'object') {
    return ctx.json(400, { ok: false, error: 'bad json' });
  }
  for (const s of body.snippets || []) {
    if (s && typeof s.code === 'string' && Buffer.byteLength(s.code, 'utf8') > MAX_SNIPPET) {
      return ctx.json(413, { ok: false, error: 'snippet too large: ' + s.name });
    }
  }
  try {
    const r = upsertEntry(body);
    return ctx.json(200, { ok: true, ...r });
  } catch (e) {
    return ctx.json(400, { ok: false, error: String(e.message) });
  }
});

app.post('/api/snippet', async (ctx) => {
  const body = await ctx.body(MAX_BODY);
  if (!body || typeof body !== 'object') {
    return ctx.json(400, { ok: false, error: 'bad json' });
  }
  const id = String(body.id || '');
  if (!SAFE_NAME.test(id)) {
    return ctx.json(400, { ok: false, error: 'bad entry id' });
  }
  try {
    const saved = saveSnippet(id, String(body.name || 'snippet.txt'), String(body.code || ''));
    return ctx.json(200, { ok: true, snippet: saved });
  } catch (e) {
    return ctx.json(400, { ok: false, error: String(e.message) });
  }
});

app.post('/api/hit', (ctx) => {
  bumpHit(ctx.query.get('id') || '');
  return ctx.json(200, { ok: true });
});

/* ---------------------------------------------------------------- 启动 */

const argv = process.argv.slice(2);
for (let i = 0; i < argv.length; i += 1) {
  if (argv[i] === '--port' && argv[i + 1]) PORT = Number(argv[++i]);
  else if (/^\d+$/.test(argv[i])) PORT = Number(argv[i]);
}

const server = app.listen(PORT, HOST);

server.on('listening', () => {
  const s = stats();
  console.log(
    '[errvault] http://' + HOST + ':' + PORT + '/  (pid ' + process.pid +
    ', ' + s.total + ' entries)'
  );
  logLine(
    'info',
    'boot',
    'listen ' + HOST + ':' + PORT +
    '  entries=' + s.total +
    '  totalCount=' + s.totalCount +
    '  unresolved=' + s.unresolved
  );
});

server.on('error', (err) => {
  console.error('[errvault] ' + err.message);
  logLine('error', 'boot', 'listen failed: ' + err.message);
  process.exit(1);
});

function shutdown(sig) {
  console.log('[errvault] shutting down (' + sig + ')');
  server.close(() => process.exit(0));
  setTimeout(() => process.exit(0), 1500).unref();
}
process.on('SIGINT', () => shutdown('SIGINT'));
process.on('SIGTERM', () => shutdown('SIGTERM'));
