/**
 * errvault 存储层：索引读写 + 分词检索 + 片段落盘。
 *
 * 被两处共用：
 *   server.mjs   HTTP 服务
 *   tools/errvault.mjs  命令行（不依赖服务是否在跑）
 */

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const APP_ROOT = path.resolve(HERE, '..');
export const DATA_DIR = path.join(APP_ROOT, 'data');
export const SNIPPET_DIR = path.join(DATA_DIR, 'snippets');
export const INDEX_FILE = path.join(DATA_DIR, 'index.json');
export const WEB_DIR = path.join(APP_ROOT, 'web');

export const MAX_SNIPPET = 256 * 1024;
export const LOG_DIR = path.join(DATA_DIR, 'logs');

/* ------------------------------------------------------------------ 枚举 */

/** 影响范围：这个坑波及多大面积。 */
export const SCOPES = {
  single: '单次任务',
  project: '单个项目',
  machine: '整机 / 环境',
  global: '跨项目通用'
};

/** 复现概率：再次遇到同样条件时重现的可能性。 */
export const REPROS = {
  always: '必现',
  often: '经常 >50%',
  sometimes: '偶发 10-50%',
  rare: '罕见 <10%'
};

/** 严重度：对任务推进的实际伤害。 */
export const SEVERITY = {
  blocker: '阻塞 · 做不下去',
  major: '严重 · 大幅拖慢或返工',
  minor: '轻微 · 绕一下就行'
};

export function ensureDirs() {
  for (const d of [DATA_DIR, SNIPPET_DIR, LOG_DIR]) {
    if (!fs.existsSync(d)) fs.mkdirSync(d, { recursive: true });
  }
}

/**
 * 追加一行运行日志。
 *
 * 按月分文件，单文件超 1 MB 轮转成 .1.log（只留一代）。
 * 日志失败绝不能影响调用方，所以全程吞异常。
 *
 * 格式：2026-11-23 15:40:01  INFO   add       ERR-xxx kind=hang scope=machine
 */
export function logLine(level, action, detail) {
  try {
    ensureDirs();
    const d = new Date();
    const ym = d.getFullYear() + String(d.getMonth() + 1).padStart(2, '0');
    const file = path.join(LOG_DIR, 'errvault-' + ym + '.log');

    try {
      if (fs.statSync(file).size > 1024 * 1024) {
        const old = path.join(LOG_DIR, 'errvault-' + ym + '.1.log');
        try {
          fs.unlinkSync(old);
        } catch (_) {
          /* 没有旧的就无所谓 */
        }
        fs.renameSync(file, old);
      }
    } catch (_) {
      /* 文件还不存在 */
    }

    const pad = (s, n) => (String(s) + ' '.repeat(n)).slice(0, n);
    const p2 = (n) => String(n).padStart(2, '0');
    // 用本地时间：toISOString 是 UTC，日志里会比实际时间差 8 小时
    const ts =
      d.getFullYear() + '-' + p2(d.getMonth() + 1) + '-' + p2(d.getDate()) + ' ' +
      p2(d.getHours()) + ':' + p2(d.getMinutes()) + ':' + p2(d.getSeconds());
    const text = typeof detail === 'string' ? detail : JSON.stringify(detail);
    fs.appendFileSync(
      file,
      ts + '  ' + pad(String(level).toUpperCase(), 5) + '  ' + pad(String(action), 9) + '  ' + text + '\n',
      'utf8'
    );
  } catch (_) {
    /* never break the caller */
  }
}

export function readIndex() {
  try {
    const raw = fs.readFileSync(INDEX_FILE, 'utf8').replace(/^\uFEFF/, '');
    const j = JSON.parse(raw);
    if (!Array.isArray(j.entries)) j.entries = [];
    return j;
  } catch {
    return { version: 1, entries: [] };
  }
}

/** 原子写：先写 .tmp 再 rename。 */
export function writeIndex(idx) {
  ensureDirs();
  const tmp = INDEX_FILE + '.tmp';
  fs.writeFileSync(tmp, JSON.stringify(idx, null, 1), 'utf8');
  fs.renameSync(tmp, INDEX_FILE);
}

export const SAFE_NAME = /^[A-Za-z0-9._\-\u4e00-\u9fff]{1,64}$/;

export function saveSnippet(entryId, name, code) {
  ensureDirs();
  if (!SAFE_NAME.test(entryId)) throw new Error('bad entry id');
  const dir = path.join(SNIPPET_DIR, entryId);
  if (!fs.existsSync(dir)) fs.mkdirSync(dir, { recursive: true });
  const safe = SAFE_NAME.test(name) ? name : 'snippet.txt';
  fs.writeFileSync(path.join(dir, safe), code, 'utf8');
  return { name: safe, size: Buffer.byteLength(code, 'utf8') };
}

export function readSnippet(entryId, name) {
  if (!SAFE_NAME.test(entryId) || !SAFE_NAME.test(name)) return null;
  const f = path.join(SNIPPET_DIR, entryId, name);
  if (!fs.existsSync(f)) return null;
  return fs.readFileSync(f, 'utf8');
}

/* ------------------------------------------------------------------ 检索 */

const CJK = /[\u4e00-\u9fff]/;

/**
 * 分词：英文/数字按词，中文按 2-gram。
 * 不引第三方分词库；对「报错原文」这种短句，2-gram 的召回已经够。
 */
export function tokenize(s) {
  const out = new Set();
  const low = String(s == null ? '' : s).toLowerCase();
  if (!low) return out;
  for (const m of low.matchAll(/[a-z0-9_.\-]{2,}/g)) out.add(m[0]);
  const cjkOnly = low.replace(/[^\u4e00-\u9fff]+/g, ' ');
  for (const seg of cjkOnly.split(/\s+/)) {
    if (!seg) continue;
    if (seg.length === 1) out.add(seg);
    for (let i = 0; i + 1 < seg.length; i += 1) out.add(seg.slice(i, i + 2));
  }
  return out;
}

// 现象和工具名权重最高：用户描述问题时，措辞最接近这两项
const FIELD_WEIGHT = {
  symptom: 3,
  tool: 3,
  cause: 2,
  fix: 2,
  tags: 2,
  goal: 1,
  kind: 1
};

/**
 * 分词缓存：id -> { stamp, sets }。
 *
 * 每个条目的 symptom/cause/fix 加起来常有几百字，中文还要按 2-gram 切，
 * 每次搜索都重新切一遍是纯浪费。用 updatedAt 当版本戳，条目一改就失效。
 * 放进程内而不是写进索引，索引体积不变，也不用做数据迁移。
 */
const tokenCache = new Map();

function tokensFor(entry, field) {
  // 版本戳用 rev 而不是 updatedAt：nowIso() 只精确到秒，
  // 同一秒内改一次的话 updatedAt 和原文一模一样，缓存不会失效。
  const stamp = String(entry.rev || 0);
  let slot = tokenCache.get(entry.id);
  if (!slot || slot.stamp !== stamp) {
    slot = { stamp, sets: {} };
    tokenCache.set(entry.id, slot);
    // 条目被删时这里不会自动收缩，给个上限兜住
    if (tokenCache.size > 500) tokenCache.clear();
  }
  if (!(field in slot.sets)) {
    const text = field === 'tags' ? (entry.tags || []).join(' ') : (entry[field] || '');
    slot.sets[field] = text ? tokenize(text) : null;
  }
  return slot.sets[field];
}

export function scoreEntry(entry, qTokens) {
  let score = 0;
  const hits = [];
  for (const [field, weight] of Object.entries(FIELD_WEIGHT)) {
    const toks = tokensFor(entry, field);
    if (!toks || toks.size === 0) continue;
    for (const t of qTokens) {
      if (toks.has(t)) {
        score += weight;
        hits.push(field + ':' + t);
      }
    }
  }
  return { score, hits: Array.from(new Set(hits)) };
}

export function search(idx, opts = {}) {
  const q = String(opts.q || '').trim();
  const limit = Math.min(Number(opts.limit) || 20, 100);
  const kindFilter = String(opts.kind || '').trim().toLowerCase();
  const toolFilter = String(opts.tool || '').trim().toLowerCase();
  const tagFilter = String(opts.tag || '').trim().toLowerCase();
  const scopeFilter = String(opts.scope || '').trim().toLowerCase();
  const reproFilter = String(opts.repro || '').trim().toLowerCase();
  const sevFilter = String(opts.severity || '').trim().toLowerCase();

  let pool = idx.entries;
  if (kindFilter) pool = pool.filter((e) => String(e.kind || '').toLowerCase() === kindFilter);
  if (toolFilter) pool = pool.filter((e) => String(e.tool || '').toLowerCase().includes(toolFilter));
  if (tagFilter) {
    pool = pool.filter((e) => (e.tags || []).some((t) => String(t).toLowerCase().includes(tagFilter)));
  }
  if (scopeFilter) pool = pool.filter((e) => String(e.scope || '').toLowerCase() === scopeFilter);
  if (reproFilter) pool = pool.filter((e) => String(e.repro || '').toLowerCase() === reproFilter);
  if (sevFilter) pool = pool.filter((e) => String(e.severity || '').toLowerCase() === sevFilter);

  if (!q) {
    return pool
      .slice()
      .sort((a, b) => String(b.at || '').localeCompare(String(a.at || '')))
      .slice(0, limit)
      .map((e) => ({ entry: e, score: 0, hits: [] }));
  }

  const qTokens = tokenize(q);
  const scored = [];
  for (const e of pool) {
    const { score, hits } = scoreEntry(e, qTokens);
    if (score > 0) scored.push({ entry: e, score, hits });
  }
  scored.sort((a, b) => {
    if (b.score !== a.score) return b.score - a.score;
    return String(b.entry.at || '').localeCompare(String(a.entry.at || ''));
  });
  return scored.slice(0, limit);
}

/* ------------------------------------------------------------------ 写入 */

export function newId() {
  const d = new Date();
  const p = (n) => String(n).padStart(2, '0');
  const stamp =
    d.getFullYear() + p(d.getMonth() + 1) + p(d.getDate()) +
    '-' + p(d.getHours()) + p(d.getMinutes()) + p(d.getSeconds());
  return 'ERR-' + stamp + '-' + Math.random().toString(36).slice(2, 8);
}

export function nowIso() {
  const d = new Date();
  const off = -d.getTimezoneOffset();
  const sign = off >= 0 ? '+' : '-';
  const oh = String(Math.floor(Math.abs(off) / 60)).padStart(2, '0');
  const om = String(Math.abs(off) % 60).padStart(2, '0');
  const local = new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 19);
  return local + sign + oh + ':' + om;
}

/**
 * 新增或更新一条。id 已存在则合并（未提供的字段保留原值）。
 * 返回 { id, isNew, entry }。
 */
export function upsertEntry(input) {
  const idx = readIndex();

  // 无 id 时先找 symptom 完全一致的旧条目：
  // 同一个坑再次踩到，应该算「又发生一次」把 count 加一，
  // 而不是在库里堆出第二条一模一样的记录。
  let existing = null;
  if (input.id) {
    existing = idx.entries.find((x) => x.id === input.id) || null;
  } else if (input.symptom) {
    const key = String(input.symptom).trim();
    if (key) {
      existing = idx.entries.find((x) => String(x.symptom || '').trim() === key) || null;
    }
  }

  const isNew = !existing; // 库里此前没有这一条
  const isRecur = !input.id && !!existing; // 同一个坑再次出现
  const id = (existing && existing.id) || input.id || newId();

  // 出现次数：新建=1；再次发生=旧值+1；只做编辑=不变
  let countValue;
  if (input.count !== undefined) countValue = Math.max(1, Number(input.count) || 1);
  else if (!existing) countValue = 1;
  else if (isRecur) countValue = (existing.count || 1) + 1;
  else countValue = existing.count || 1;

  const snippets = [];
  for (const s of input.snippets || []) {
    if (!s || !s.name || typeof s.code !== 'string') continue;
    if (Buffer.byteLength(s.code, 'utf8') > MAX_SNIPPET) continue;
    snippets.push(saveSnippet(id, s.name, s.code));
  }

  const pick = (k, dflt) =>
    input[k] !== undefined ? input[k] : (existing && existing[k] !== undefined ? existing[k] : dflt);

  const entry = {
    id,
    at: input.at || (existing && existing.at) || nowIso(),
    updatedAt: nowIso(),
    // 单调递增的版本号：给检索缓存当失效戳。
    // 不能只靠 updatedAt —— 它只精确到秒，同秒内的修改无法区分。
    rev: existing ? (existing.rev || 0) + 1 : 1,
    kind: pick('kind', 'error'),
    tool: pick('tool', ''),
    goal: pick('goal', ''),
    symptom: pick('symptom', ''),
    cause: pick('cause', ''),
    fix: pick('fix', ''),
    // —— 新增的三个实用维度 ——
    scope: pick('scope', 'project'), // 影响范围 single/project/machine/global
    repro: pick('repro', 'sometimes'), // 复现概率 always/often/sometimes/rare
    severity: pick('severity', 'major'), // 严重度 blocker/major/minor
    // —— 自动维护 ——
    count: countValue, // 出现次数
    firstSeen: existing ? existing.firstSeen || existing.at || nowIso() : nowIso(),
    lastSeen: nowIso(),
    tags: Array.isArray(input.tags) ? input.tags : (existing ? existing.tags || [] : []),
    snippets: (existing && existing.snippets ? existing.snippets.slice() : []).concat(snippets),
    resolved: input.resolved === undefined ? (existing ? existing.resolved : true) : !!input.resolved,
    hits: pick('hits', 0)
  };

  if (isNew) {
    idx.entries.push(entry);
  } else {
    const i = idx.entries.findIndex((x) => x.id === id);
    if (i >= 0) idx.entries[i] = entry;
    else idx.entries.push(entry);
  }
  writeIndex(idx);
  logLine(
    'info',
    isRecur ? 'recur' : isNew ? 'add' : 'update',
    id +
      '  kind=' + entry.kind +
      '  scope=' + entry.scope +
      '  repro=' + entry.repro +
      '  severity=' + entry.severity +
      '  count=' + entry.count +
      '  tool=' + (entry.tool || '-')
  );
  return { id, isNew, isRecur, entry };
}

export function bumpHit(id) {
  const idx = readIndex();
  const e = idx.entries.find((x) => x.id === id);
  if (!e) return;
  e.hits = (e.hits || 0) + 1;
  e.lastHitAt = nowIso();
  writeIndex(idx);
  logLine('info', 'hit', id + '  hits=' + e.hits);
}

export function stats() {
  const idx = readIndex();
  const byKind = {};
  const byTool = {};
  const byTag = {};
  const byScope = {};
  const byRepro = {};
  const bySeverity = {};
  let totalCount = 0;

  for (const e of idx.entries) {
    const k = e.kind || 'unknown';
    byKind[k] = (byKind[k] || 0) + 1;
    const t = e.tool || 'unknown';
    byTool[t] = (byTool[t] || 0) + 1;
    for (const tg of e.tags || []) byTag[tg] = (byTag[tg] || 0) + 1;

    const sc = e.scope || 'project';
    byScope[sc] = (byScope[sc] || 0) + 1;
    const rp = e.repro || 'sometimes';
    byRepro[rp] = (byRepro[rp] || 0) + 1;
    const sv = e.severity || 'major';
    bySeverity[sv] = (bySeverity[sv] || 0) + 1;

    totalCount += e.count || 1;
  }

  const resolved = idx.entries.filter((e) => e.resolved !== false).length;

  // 反复踩的坑才值得优先根治，这里单独拎出来
  const topRecur = idx.entries
    .slice()
    .sort((a, b) => (b.count || 1) - (a.count || 1))
    .filter((e) => (e.count || 1) > 1)
    .slice(0, 5)
    .map((e) => ({
      id: e.id,
      count: e.count || 1,
      symptom: String(e.symptom || '').slice(0, 60)
    }));

  return {
    total: idx.entries.length,
    totalCount, // 累计出现次数（同一问题重复发生会累加）
    resolved,
    unresolved: idx.entries.length - resolved,
    byKind,
    byTool,
    byTag,
    byScope,
    byRepro,
    bySeverity,
    topRecur
  };
}
