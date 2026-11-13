#!/usr/bin/env node
/**
 * @name stop
 * @category system
 * @purpose Stop the deployed services: prefer the recorded pid file, fall back to
 *          whatever is listening on the configured port.
 * @input  [codevault|pulse|tuner|all]
 * @usage  node tools/stop.mjs all
 * @reuse  yes
 *
 * Killing by port is intentionally narrow: only the PID that owns that exact
 * listener is signalled, never every node.exe on the machine.
 */

import fs from 'node:fs';
import net from 'node:net';
import path from 'node:path';
import { execFile } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const KIT_ROOT = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const CONFIG_FILE = path.join(KIT_ROOT, 'config', 'deploy.json');
const STATE_DIR = path.join(KIT_ROOT, 'state');

const APP_PORTS = {
  codevault: (cfg) => cfg.ports.codevault,
  pulse: (cfg) => cfg.ports.pulse,
  tuner: (cfg) => cfg.ports.tuner
};

function readConfig() {
  try {
    // Strip a possible BOM (see launch.mjs) before parsing.
    const raw = fs.readFileSync(CONFIG_FILE, 'utf8').replace(/^\uFEFF/, '');
    return JSON.parse(raw);
  } catch {
    return { ports: { codevault: 47910, pulse: 47821, tuner: 47822 } };
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

function killPid(pid) {
  try {
    process.kill(pid);
    return true;
  } catch {
    return false;
  }
}

async function stopOne(name, cfg) {
  const pidFile = path.join(STATE_DIR, name + '.pid');
  let pid = 0;

  try {
    pid = Number(fs.readFileSync(pidFile, 'utf8').trim());
  } catch {
    /* fall through to the port lookup */
  }

  let stopped = false;
  if (pid && killPid(pid)) stopped = true;

  if (!stopped) {
    const fromPort = await pidsOnPort(APP_PORTS[name](cfg));
    for (const candidate of fromPort) {
      if (killPid(candidate)) stopped = true;
    }
  }

  try {
    fs.unlinkSync(pidFile);
  } catch {
    /* already gone */
  }

  console.log((stopped ? '[stop] ' : '[skip] ') + name + (stopped ? ' 已停止' : ' 未在运行'));
  return stopped;
}

async function main() {
  const target = (process.argv[2] || 'all').toLowerCase();
  const cfg = readConfig();
  const names = target === 'all' ? ['codevault', 'pulse', 'tuner'] : [target];

  if (!names.every((n) => APP_PORTS[n])) {
    console.error('未知目标：' + target);
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
