#!/usr/bin/env node
/**
 * @name errvault
 * @category system
 * @purpose CodeBuddy 报错库的命令行入口。遇到报错 / 卡死 / 被拦截时先 search，
 *          解决后 add（附最小复现代码）。不依赖服务进程是否在跑。
 * @input  add | search | list | show | stats
 * @usage  node tools/errvault.mjs search "Access is denied"
 *         node tools/errvault.mjs add --kind error --tool Bash --goal "移动文件" \
 *              --symptom "Access is denied" --cause "文件被占用" --fix "先停进程再删" \
 *              --tags filelock,permission --code-file D:\tmp\repro.mjs
 * @reuse  yes
 *
 * 退出码：0 正常；1 用法错误；2 无匹配（search/list，便于 && 串联判断）
 */

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import {
  readIndex,
  search,
  stats,
  upsertEntry,
  readSnippet,
  SCOPES,
  REPROS,
  SEVERITY,
  LOG_DIR
} from '../apps/errvault/lib/store.mjs';

const SCOPE_LABEL = SCOPES;
const REPRO_LABEL = REPROS;
const SEV_LABEL = SEVERITY;

const HERE = path.dirname(fileURLToPath(import.meta.url));
const KIT_ROOT = path.resolve(HERE, '..');

/* ------------------------------------------------------------------ 参数 */

function parseArgs(argv) {
  const out = { _: [] };
  for (let i = 0; i < argv.length; i += 1) {
    const a = argv[i];
    if (a.startsWith('--')) {
      const key = a.slice(2);
      const next = argv[i + 1];
      if (next !== undefined && !next.startsWith('--')) {
        out[key] = next;
        i += 1;
      } else {
        out[key] = true;
      }
    } else {
      out._.push(a);
    }
  }
  return out;
}

const KIND_LABEL = {
  error: '报错',
  hang: '卡死',
  blocked: '被拦截',
  'tool-fail': '工具失败'
};

function line(char = '─', n = 72) {
  return char.repeat(n);
}

function fmtDate(iso) {
  if (!iso) return '';
  return String(iso).replace('T', ' ').slice(0, 19);
}

/* ------------------------------------------------------------------ 命令 */

function cmdAdd(args) {
  if (!args.symptom) {
    console.error('缺少 --symptom（现象 / 报错原文），它是检索主字段。');
    return 1;
  }
  // 只把「显式传了」的字段放进 payload。
  // 若全填默认值，第二次只带 --symptom 时会把库里的
  // 范围 / 复现率 / 严重度 / 根因 / 做法统统冲成默认值。
  const payload = { symptom: String(args.symptom) };
  for (const k of ['kind', 'tool', 'goal', 'cause', 'fix', 'scope', 'repro', 'severity']) {
    if (args[k] !== undefined) payload[k] = args[k];
  }
  if (args.tags !== undefined) {
    payload.tags = String(args.tags).split(',').map((s) => s.trim()).filter(Boolean);
  }
  if (args.resolved !== undefined) payload.resolved = args.resolved !== 'false';
  if (args.count !== undefined) payload.count = Number(args.count);

  if (args['code-file']) {
    const p = path.resolve(process.cwd(), String(args['code-file']));
    if (!fs.existsSync(p)) {
      console.error('代码文件不存在: ' + p);
      return 1;
    }
    const code = fs.readFileSync(p, 'utf8');
    payload.snippets = [{ name: args['code-name'] || path.basename(p), code }];
  }

  const r = upsertEntry(payload);
  const e = r.entry;
  const head = r.isNew
    ? '已记录'
    : r.isRecur
      ? '同一问题第 ' + e.count + ' 次出现，已累加'
      : '已更新';
  console.log(head + '：' + r.id);
  console.log('  类型  ' + (KIND_LABEL[e.kind] || e.kind));
  console.log(
    '  范围  ' + (SCOPE_LABEL[e.scope] || e.scope) +
    '   复现  ' + (REPRO_LABEL[e.repro] || e.repro) +
    '   严重  ' + (SEV_LABEL[e.severity] || e.severity) +
    '   出现  ' + e.count + ' 次'
  );
  if (e.tool) console.log('  工具  ' + e.tool);
  if (e.goal) console.log('  目的  ' + e.goal);
  console.log('  现象  ' + e.symptom.slice(0, 100));
  if (e.cause) console.log('  根因  ' + e.cause.slice(0, 100));
  if (e.fix) console.log('  做法  ' + e.fix.slice(0, 100));
  if (e.snippets.length) {
    console.log('  代码  ' + e.snippets.map((s) => s.name + '(' + s.size + 'B)').join(', '));
  }
  return 0;
}

function printRow(row, index) {
  const e = row.entry;
  const flag = e.resolved === false ? '[未解决]' : '[已解决]';
  const score = row.score ? ' 匹配' + row.score : '';
  console.log('');
  console.log(index + '. ' + flag + ' ' + (KIND_LABEL[e.kind] || e.kind) + score + '  ' + fmtDate(e.at));
  console.log(
    '   维度  ' + (SCOPE_LABEL[e.scope] || e.scope || '-') +
    ' / ' + (REPRO_LABEL[e.repro] || e.repro || '-') +
    ' / ' + (SEV_LABEL[e.severity] || e.severity || '-') +
    '   出现 ' + (e.count || 1) + ' 次' +
    (e.hits ? '，被引用 ' + e.hits + ' 次' : '')
  );
  if (e.tool) console.log('   工具  ' + e.tool);
  if (e.goal) console.log('   目的  ' + e.goal);
  console.log('   现象  ' + String(e.symptom || '').replace(/\s+/g, ' ').slice(0, 160));
  if (e.cause) console.log('   根因  ' + String(e.cause).replace(/\s+/g, ' ').slice(0, 160));
  if (e.fix) console.log('   做法  ' + String(e.fix).replace(/\s+/g, ' ').slice(0, 200));
  if (e.tags && e.tags.length) console.log('   标签  ' + e.tags.join(', '));
  if (row.hits && row.hits.length) console.log('   命中  ' + row.hits.join(', '));
  if (e.snippets && e.snippets.length) {
    console.log('   代码  ' + e.snippets.map((s) => s.name).join(', ') + '  （node tools/errvault.mjs show ' + e.id + ' 看全文）');
  }
  console.log('   id    ' + e.id);
}

function cmdSearch(args) {
  const q = args._.join(' ') || args.q || '';
  if (!q) {
    console.error('用法: node tools/errvault.mjs search "<关键词>"');
    return 1;
  }
  const idx = readIndex();
  const rows = search(idx, {
    q,
    kind: args.kind,
    tool: args.tool,
    tag: args.tag,
    scope: args.scope,
    repro: args.repro,
    severity: args.severity,
    limit: args.limit || 5
  });
  if (!rows.length) {
    console.log('未命中：「' + q + '」');
    console.log('（这是新问题，解决后请用 add 记录下来）');
    return 2;
  }
  console.log(line('='));
  console.log('报错库命中 ' + rows.length + ' 条（按匹配度）');
  console.log(line('='));
  rows.forEach((r, i) => printRow(r, i + 1));
  console.log('');
  return 0;
}

function cmdList(args) {
  const idx = readIndex();
  const rows = search(idx, { limit: args.limit || 15, kind: args.kind });
  if (!rows.length) {
    console.log('库为空。');
    return 2;
  }
  console.log('最近 ' + rows.length + ' 条：');
  rows.forEach((r, i) => printRow(r, i + 1));
  console.log('');
  return 0;
}

function cmdShow(args) {
  const id = args._[0];
  if (!id) {
    console.error('用法: node tools/errvault.mjs show <id>');
    return 1;
  }
  const idx = readIndex();
  const e = idx.entries.find((x) => x.id === id);
  if (!e) {
    console.error('找不到 ' + id);
    return 2;
  }
  console.log(line('='));
  console.log(e.id + '   ' + (KIND_LABEL[e.kind] || e.kind) + '   ' + fmtDate(e.at));
  console.log(line('='));
  for (const [label, key] of [
    ['工具', 'tool'],
    ['目的', 'goal'],
    ['现象', 'symptom'],
    ['根因', 'cause'],
    ['正确做法', 'fix']
  ]) {
    if (e[key]) console.log('\n[' + label + ']\n' + e[key]);
  }
  if (e.tags && e.tags.length) console.log('\n[标签] ' + e.tags.join(', '));
  console.log('\n[状态] ' + (e.resolved === false ? '未解决' : '已解决') +
    (e.hits ? '   被引用 ' + e.hits + ' 次' : ''));
  for (const s of e.snippets || []) {
    const code = readSnippet(e.id, s.name);
    console.log('\n[代码] ' + s.name + '  (' + s.size + ' B)');
    console.log(line('-'));
    console.log(code == null ? '(读取失败)' : code);
    console.log(line('-'));
  }
  console.log('');
  return 0;
}

function cmdStats() {
  const s = stats();
  console.log(
    '共 ' + s.total + ' 条记录，累计出现 ' + (s.totalCount || s.total) + ' 次' +
    '：已解决 ' + s.resolved + '，未解决 ' + s.unresolved
  );

  const show = (title, obj) => {
    const keys = Object.keys(obj || {});
    if (!keys.length) return;
    console.log('\n' + title);
    keys.sort((a, b) => obj[b] - obj[a]).forEach((k) => console.log('  ' + obj[k] + '  ' + k));
  };
  const relabel = (obj, map) =>
    Object.fromEntries(Object.entries(obj || {}).map((kv) => [map[kv[0]] || kv[0], kv[1]]));

  show('按类型', s.byKind);
  show('按影响范围', relabel(s.byScope, SCOPE_LABEL));
  show('按复现概率', relabel(s.byRepro, REPRO_LABEL));
  show('按严重度', relabel(s.bySeverity, SEV_LABEL));
  show('按工具', s.byTool);
  show('按标签', s.byTag);

  if (s.topRecur && s.topRecur.length) {
    console.log('\n反复踩的坑（优先根治）');
    s.topRecur.forEach((x) => console.log('  ' + String(x.count).padStart(3) + '×  ' + x.symptom));
  }

  console.log('');
  return 0;
}

function cmdLogs(args) {
  const tail = Math.min(Number(args.tail) || 40, 500);
  const d = new Date();
  const ym = d.getFullYear() + String(d.getMonth() + 1).padStart(2, '0');
  const file = path.join(LOG_DIR, 'errvault-' + ym + '.log');
  if (!fs.existsSync(file)) {
    console.log('本月还没有日志：' + file);
    return 2;
  }
  const lines = fs
    .readFileSync(file, 'utf8')
    .replace(/\r/g, '')
    .trimEnd()
    .split('\n');
  console.log('文件 ' + path.basename(file) + '  共 ' + lines.length + ' 行，显示最后 ' + tail + ' 行：');
  console.log(line('-', 88));
  lines.slice(-tail).forEach((l) => console.log('  ' + l));
  return 0;
}

/* ------------------------------------------------------------------ 入口 */

const argv = process.argv.slice(2);
const command = argv[0];
const args = parseArgs(argv.slice(1));

const COMMANDS = {
  add: cmdAdd,
  search: cmdSearch,
  find: cmdSearch,
  list: cmdList,
  ls: cmdList,
  show: cmdShow,
  stats: cmdStats,
  logs: cmdLogs
};

function usage() {
  console.log([
    'CodeBuddy 报错库 · errvault',
    '',
    '用法：node tools/errvault.mjs <命令> [参数]',
    '',
    '  search "<关键词>"            先查库（遇到问题第一步）',
    '  add --symptom "..." \\        解决后记录',
    '      [--kind error|hang|blocked|tool-fail]',
    '      [--scope single|project|machine|global]      影响范围',
    '      [--repro always|often|sometimes|rare]        复现概率',
    '      [--severity blocker|major|minor]             严重度',
    '      [--tool Bash] [--goal "..."] [--cause "..."] [--fix "..."]',
    '      [--tags a,b] [--code-file <路径>] [--code-name name.txt]',
    '  list [--limit N]             最近记录',
    '  show <id>                    看详情与代码',
    '  stats                        统计（含范围/复现率/严重度分布）',
    '  logs [--tail 40]             看本月运行日志',
    '',
    '说明：',
    '  同一个 --symptom 再次 add 时，不会新建记录，而是把「出现次数」加一。',
    '  查询/写入/hit 都会写进 data/logs/errvault-YYYYMM.log。',
    '',
    '示例：',
    '  node tools/errvault.mjs search "Access is denied 文件占用"',
    '  node tools/errvault.mjs add --kind blocked --tool Bash --goal "删除目录" \\',
    '       --symptom "The process cannot access the file" \\',
    '       --scope machine --repro always --severity major \\',
    '       --cause "dll 被 explorer 加载" --fix "改名目录再删，或重启后删"',
    ''
  ].join('\n'));
}

if (!command || command === 'help' || command === '--help' || command === '-h') {
  usage();
  process.exit(command ? 0 : 1);
}

const fn = COMMANDS[command];
if (!fn) {
  console.error('未知命令: ' + command);
  usage();
  process.exit(1);
}

try {
  process.exit(fn(args));
} catch (e) {
  console.error('执行失败: ' + (e && e.message));
  process.exit(1);
}
