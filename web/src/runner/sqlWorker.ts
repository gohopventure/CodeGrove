/**
 * The SQL runner's Web Worker. Bundled on its own by vite-plugins/sqlRunner.ts into
 * /sql-runner/worker.js (a classic worker, not a module, so it can importScripts sql.js) and started
 * by public/sql-runner/host.js on the preview origin, never the app's.
 *
 * It loads SQLite (sql.js: sql-wasm.js + sql-wasm.wasm, served next to it), then removes its own
 * network and storage functions, then answers runs: { type: 'run', id, request } → { type: 'result', id, result }.
 * The engine (sqlEngine.ts) is the same code Vitest tests.
 */
/// <reference lib="webworker" />
import type { SqlJsStatic } from 'sql.js';
import { runSql, type SqlRunRequest } from './sqlEngine';

declare const self: DedicatedWorkerGlobalScope;
declare function initSqlJs(config: { locateFile: (file: string) => string }): Promise<SqlJsStatic>;

const NETWORK_AND_STORAGE = [
  'fetch', 'XMLHttpRequest', 'WebSocket', 'WebSocketStream', 'EventSource', 'WebTransport', 'importScripts',
  'Worker', 'SharedWorker', 'BroadcastChannel', 'indexedDB', 'caches',
];

/** Same lock as the C# worker: SQL can't reach the network anyway, but nothing here should be able to. */
function lockDown() {
  const scope = self as unknown as Record<string, unknown>;
  for (const name of NETWORK_AND_STORAGE) {
    for (let target: object | null = self; target; target = Object.getPrototypeOf(target)) {
      try {
        if (Object.prototype.hasOwnProperty.call(target, name)) delete (target as Record<string, unknown>)[name];
      } catch {
        /* not configurable: shadowed below */
      }
    }
    try {
      Object.defineProperty(scope, name, { value: undefined, writable: false, configurable: false });
    } catch {
      /* already locked */
    }
  }
}

async function start() {
  let SQL: SqlJsStatic;
  try {
    self.importScripts('sql-wasm.js');
    SQL = await initSqlJs({ locateFile: (file) => file });
  } catch (error) {
    self.postMessage({ type: 'failed', detail: 'SQL could not start: ' + (error instanceof Error ? error.message : String(error)) });
    return;
  }
  lockDown();
  self.addEventListener('message', (event: MessageEvent) => {
    const msg = event.data as { type?: string; id?: string; request?: SqlRunRequest };
    if (msg?.type !== 'run' || !msg.request) return;
    try {
      self.postMessage({ type: 'result', id: msg.id, result: runSql(SQL, msg.request) });
    } catch (error) {
      self.postMessage({ type: 'result', id: msg.id, crashed: "Codegrove's SQL runner hit a problem (our bug, not yours): " + (error instanceof Error ? error.message : String(error)) });
    }
  });
  self.postMessage({ type: 'ready' });
}

void start();
