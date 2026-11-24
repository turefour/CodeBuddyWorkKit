#!/usr/bin/env node
/**
 * @name stop
 * @category system
 * @purpose Stop the deployed services: prefer the recorded pid file, but always
 *          verify the target really is ours before signalling it.
 * @input  [codevault|pulse|tuner|errvault|all] [--force]
 * @usage  node tools/stop.mjs all
 * @reuse  yes
 *
 * 为什么不能只按端口杀：
 *   端口是共享资源。用户可能自己起了一个开发服务器占着 47821，
 *   或者上一次异常退出留下了别的程序。进程级 kill 在 Windows 上是
 *   TerminateProcess —— 目标没有任何清理机会，未保存的工作直接丢。
 *
 * 判定顺序（任一不满足就不杀）：
 *   1. pid 文件里的 pid 必须在目标端口上监听
 *   2. 该端口的 /api/health 必须自报是本套件的服务
 *   3. 端口被非本套件进程占用时，明确报冲突并跳过
 *
 * --force：跳过 health 校验，按端口强杀（仅在服务半死、health 无响应时用）。
 */

import fs from 'node:fs';
import path from 'node:path';
import { execFile } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const KIT_ROOT = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const CONFIG_FILE = path.join(KIT_ROOT, 'config', 'deploy.json');
const STATE_DIR = path.join(KIT_ROOT, 'state');

const APP_PORTS = {
  codevault: (cfg) => cfg.ports.codevault,
  pulse: (cfg) => cfg.ports.pulse,
  tuner: (cfg) => cfg.ports.tuner,
  errvault: (cfg) => cfg.ports.errvault
};

const FORCE = process.argv.includes('--force');

function readConfig() {
  try {
    // Strip a possible BOM (see launch.mjs) before parsing.
    const raw = fs.readFileSync(CONFIG_FILE, 'utf8').replace(/^\uFEFF/, '');
    return JSON.parse(raw);
  } catch {
    // 兜底默认值要和 deploy.json 的默认端口一致
    return { ports: { codevault: 47910, pulse: 47821, tuner: 47822, errvault: 47911 } };
  }
}

/** PIDs currently listening on a local port (Windows netstat). */
function pidsOnPort(port) {
  return new Promise((resolve) => {
    execFile('netstat', ['-ano', '-p', 'TCP'], { windowsHide: true, timeout: 5000 }, (err, stdout) => {
      if (err || !stdout) return resolve([]);
      const pids = new Set();
      for (const line of String(stdout).split('\n')) {
        const cols = line.trim().split(/\s+/);
        if (cols.length < 5) continue;
        const local = cols[1] || '';
        const state = cols[3] || '';
        const pid = Number(cols[4]);
        if (state !== 'LISTENING') continue;
        if (local.endsWith(':' + port)) pids.add(pid);
      }
      resolve(Array.from(pids));
    });
  });
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
 * 判断占用该端口的到底是不是本套件的服务。与 launch.mjs 的 isOurs 保持一致。
 *   codevault/errvault: { ok, app: '<name>', port }
 *   pulse/tuner:        { ok, version, port, pid }
 */
function isOurs(health, name, port) {
  if (!health || health.ok !== true) return false;
  if (health.app === name) return true;
  return Boolean(health.version) && health.port === port;
}

function killPid(pid) {
  try {
    process.kill(pid);
    return true;
  } catch {
    return false;
  }
}

function removePidFile(file) {
  try {
    fs.unlinkSync(file);
  } catch {
    /* already gone */
  }
}

async function stopOne(name, cfg) {
  const port = APP_PORTS[name](cfg);
  const pidFile = path.join(STATE_DIR, name + '.pid');

  let pidFromFile = 0;
  try {
    pidFromFile = Number(fs.readFileSync(pidFile, 'utf8').trim());
  } catch {
    /* no pid file: fall through to the port lookup */
  }

  const listeners = await pidsOnPort(port);
  const health = await readHealth(port);

  // 端口没人监听：服务本来就没跑，清掉可能残留的 pid 文件即可
  if (!listeners.length) {
    removePidFile(pidFile);
    console.log('[skip] ' + name + ' 未在运行');
    return false;
  }

  const ours = isOurs(health, name, port);

  if (!ours && !FORCE) {
    // 端口被别的程序占着 —— 这是最容易出事的分支，必须明确拒绝
    removePidFile(pidFile); // pid 文件已经没意义了
    console.error(
      '[conflict] ' + name + ' 的端口 ' + port + ' 被其他程序占用，已跳过（不会杀）。' +
      (health ? ' 响应：' + JSON.stringify(health).slice(0, 80) : ' 无 HTTP 响应')
    );
    console.error('           确认要强杀请加 --force。');
    return false;
  }

  if (!ours && FORCE) {
    console.error('[force] ' + name + ' 的 health 未确认身份，按端口强杀');
  }

  // 优先用 pid 文件里的 pid，但必须确认它确实在监听我们的端口
  const targets = [];
  if (pidFromFile && listeners.includes(pidFromFile)) {
    targets.push(pidFromFile);
  } else {
    if (pidFromFile) {
      console.error(
        '[stale] ' + name + '.pid 记录的是 pid ' + pidFromFile +
        '，但端口 ' + port + ' 的监听者是 ' + listeners.join(', ') + '，以端口为准'
      );
    }
    targets.push(...listeners);
  }

  let stopped = false;
  for (const pid of targets) {
    if (killPid(pid)) stopped = true;
  }

  removePidFile(pidFile);
  console.log((stopped ? '[stop] ' : '[fail] ') + name + (stopped ? ' 已停止' : ' 停止失败'));
  return stopped;
}

async function main() {
  const target = (process.argv[2] || 'all').toLowerCase();
  const cfg = readConfig();
  // 用 Object.keys(APP_PORTS)：新增服务会自动纳入「全部停止」
  const names = target === 'all' ? Object.keys(APP_PORTS) : [target];

  if (!names.every((n) => APP_PORTS[n])) {
    console.error('未知目标：' + target + '（可选 ' + Object.keys(APP_PORTS).join(' / ') + ' / all）');
    process.exit(2);
  }

  let count = 0;
  for (const name of names) {
    if (await stopOne(name, cfg)) count += 1;
  }
  console.log('共停止 ' + count + ' 个服务。');
}

main().catch((err) => {
  console.error('[stop] ' + err.message);
  process.exit(1);
});
