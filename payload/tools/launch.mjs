#!/usr/bin/env node
/**
 * @name launch
 * @category system
 * @purpose Start one or all deployed services from config/deploy.json, wait until
 *          health checks pass, and open the dashboard in the default browser.
 * @input  <codevault|pulse|tuner|all> [--no-open] [--timeout 20]
 * @usage  node tools/launch.mjs codevault
 *         node tools/launch.mjs all --no-open
 * @reuse  yes
 *
 * Everything is driven by config/deploy.json: ports and bind address are never
 * hard-coded, so a copy deployed to another machine only edits that one file.
 */

import fs from 'node:fs';
import path from 'node:path';
import net from 'node:net';
import { spawn, execFile } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const KIT_ROOT = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const CONFIG_FILE = path.join(KIT_ROOT, 'config', 'deploy.json');
const STATE_DIR = path.join(KIT_ROOT, 'state');

const APPS = {
  codevault: {
    label: '索引库 CodeVault',
    entry: path.join('apps', 'codevault', 'tools', 'serve.mjs'),
    args: () => [],
    port: (cfg) => cfg.ports.codevault,
    env: (cfg) => ({
      CODEVAULT_PORT: String(cfg.ports.codevault),
      CODEVAULT_HOST: cfg.host
    }),
    open: false
  },
  pulse: {
    label: '进度看板 Pulse',
    entry: path.join('apps', 'pulse', 'server', 'pulse-server.js'),
    args: (cfg) => ['--port', String(cfg.ports.pulse)],
    port: (cfg) => cfg.ports.pulse,
    env: (cfg) => ({ CODEBUDDY_PULSE_HOST: cfg.host }),
    open: false
  },
  tuner: {
    label: '降温面板 Tuner',
    entry: path.join('apps', 'tuner', 'server', 'tuner-server.js'),
    args: (cfg) => ['--port', String(cfg.ports.tuner)],
    port: (cfg) => cfg.ports.tuner,
    env: (cfg) => ({ CODEBUDDY_TUNER_HOST: cfg.host }),
    open: false
  },
  errvault: {
    label: '报错库 errvault',
    entry: path.join('apps', 'errvault', 'server.mjs'),
    args: (cfg) => ['--port', String(cfg.ports.errvault)],
    port: (cfg) => cfg.ports.errvault,
    env: (cfg) => ({ ERRVAULT_HOST: cfg.host }),
    open: false
  }
};

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

function readConfig() {
  try {
    // A config written by PowerShell 5.1 or edited in Notepad may carry a UTF-8
    // BOM, and JSON.parse rejects it — strip it before parsing.
    const raw = fs.readFileSync(CONFIG_FILE, 'utf8').replace(/^\uFEFF/, '');
    return JSON.parse(raw);
  } catch (err) {
    console.error('[launch] 读取配置失败：' + CONFIG_FILE);
    console.error('         ' + err.message);
    console.error('         请先运行安装器完成部署。');
    process.exit(2);
  }
}

function portOpen(port, host = '127.0.0.1') {
  return new Promise((resolve) => {
    const socket = net.connect({ port, host });
    socket.setTimeout(800);
    socket.once('connect', () => {
      socket.destroy();
      resolve(true);
    });
    socket.once('timeout', () => {
      socket.destroy();
      resolve(false);
    });
    socket.once('error', () => resolve(false));
  });
}

async function healthOk(url, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const res = await fetch(url, { signal: AbortSignal.timeout(1500) });
      if (res.ok) return true;
    } catch {
      /* not up yet */
    }
    // 150ms 一轮：Node 冷启动约 150-400ms，400ms 的间隔平均多等 200ms
    await sleep(150);
  }
  return false;
}

function openBrowser(url) {
  execFile('cmd', ['/c', 'start', '', url], { windowsHide: true }, () => {});
}

function startApp(name, cfg) {
  const app = APPS[name];
  const entry = path.join(KIT_ROOT, app.entry);
  if (!fs.existsSync(entry)) {
    return { name, ok: false, error: '找不到入口：' + entry };
  }
  fs.mkdirSync(STATE_DIR, { recursive: true });

  const child = spawn(process.execPath, [entry, ...app.args(cfg)], {
    cwd: path.dirname(entry),
    detached: true,
    stdio: 'ignore',
    windowsHide: true,
    env: { ...process.env, ...app.env(cfg) }
  });
  child.unref();

  try {
    fs.writeFileSync(path.join(STATE_DIR, name + '.pid'), String(child.pid), 'utf8');
  } catch {
    /* pid file is a convenience only */
  }
  return { name, ok: true, pid: child.pid };
}

/** 读一次 /api/health，拿不到就返回 null。 */
async function readHealth(port) {
  try {
    const res = await fetch('http://127.0.0.1:' + port + '/api/health', {
      signal: AbortSignal.timeout(1500)
    });
    if (!res.ok) return null;
    return await res.json();
  } catch {
    return null;
  }
}

/**
 * 判断占用该端口的到底是不是本套件的服务。
 *
 * 之前只看端口开没开就 `[skip]`，结果「别的实例（比如开发目录里那个）
 * 占着端口」被当成「已经在跑」，用户看到的是「服务只启动了一部分」。
 * 现在必须身份对得上才算已运行，否则明确报端口冲突。
 *
 *   codevault:  { ok, app: 'codevault', port }
 *   pulse/tuner: { ok, version, port, pid, uptimeMs }
 */
function isOurs(health, name, port) {
  if (!health || health.ok !== true) return false;
  if (health.app === name) return true;
  return Boolean(health.version) && health.port === port;
}

async function ensure(name, cfg, timeoutMs) {
  const app = APPS[name];
  const port = app.port(cfg);

  if (await portOpen(port)) {
    const health = await readHealth(port);
    if (isOurs(health, name, port)) {
      console.log('[skip] ' + app.label + ' 已在 ' + port + ' 端口运行');
      return { name, ok: true, started: false, port };
    }

    console.error(
      '[conflict] 端口 ' + port + ' 被占用，但不是本套件的服务：' +
      (health ? JSON.stringify(health).slice(0, 90) : '（无 HTTP 响应）')
    );
    console.error('           请换一个端口，或先结束占用该端口的进程。');
    return { name, ok: false, started: false, port, error: 'port-conflict' };
  }

  console.log('[start] ' + app.label + ' → 端口 ' + port);
  const started = startApp(name, cfg);
  if (!started.ok) return { ...started, port };

  const url = 'http://127.0.0.1:' + port + '/';
  const healthy = await healthOk(
    app.health ? app.health(cfg) : url + 'api/health',
    timeoutMs
  );
  console.log(healthy ? '[ok]   ' + app.label + ' 已就绪 ' + url : '[fail] ' + app.label + ' 启动超时');
  return { name, ok: healthy, started: true, port, url };
}

async function main() {
  const argv = process.argv.slice(2);
  const target = (argv[0] || 'codevault').toLowerCase();
  const noOpen = argv.includes('--no-open');
  const timeoutArg = argv.indexOf('--timeout');
  const timeoutMs = (timeoutArg >= 0 ? Number(argv[timeoutArg + 1]) : 20) * 1000;

  const cfg = readConfig();
  // 用 Object.keys(APPS) 而不是写死数组：以后新增服务不用再回来改这一行
  const names = target === 'all' ? Object.keys(APPS) : [target];
  if (!names.every((n) => APPS[n])) {
    console.error('未知目标：' + target + '（可选 ' + Object.keys(APPS).join(' / ') + ' / all）');
    process.exit(2);
  }

  console.log('部署目录：' + KIT_ROOT);
  console.log('监听地址：' + cfg.host + (cfg.lan ? '（已允许局域网访问）' : '（仅本机）'));

  // 并行启动：4 个服务同时 ensure，总耗时≈最慢的那个。
  // 之前是 for + await 串行，冷启动要 4 份等待累加（实测 6-10 秒）。
  const results = await Promise.all(names.map((n) => ensure(n, cfg, timeoutMs)));

  const failed = results.filter((r) => !r.ok);
  const dash = results.find((r) => r.name === 'codevault' && r.ok);
  if (dash && !noOpen && APPS.codevault.open) {
    openBrowser(dash.url || 'http://127.0.0.1:' + cfg.ports.codevault + '/');
  }

  if (failed.length) {
    console.error('有服务未启动：' + failed.map((r) => r.name).join(', '));
    process.exit(1);
  }
  console.log('全部就绪。');
}

main().catch((err) => {
  console.error('[launch] ' + err.message);
  process.exit(1);
});
