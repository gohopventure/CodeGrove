/**
 * Runs learner SQL in the browser. Same design as the C# runner (csharp.ts): ONE hidden iframe for the
 * session, PREVIEW_ORIGIN/sql-runner/index.html, which starts a Web Worker running SQLite compiled to
 * WebAssembly (sql.js) and the engine in sqlEngine.ts. The first SQL lesson downloads ~0.7 MB (then
 * cached); a run takes a few milliseconds. Every run builds a fresh database from the challenge's
 * hidden .setup.sql, so nothing a learner does carries over between runs.
 */
import { useSyncExternalStore } from 'react';
import { PREVIEW_ORIGIN } from './origin';
import type { SqlRunRequest, SqlRunResult } from './sqlEngine';
import type { TestResult, TestSpec } from './types';

export type SqlStatus = 'idle' | 'loading' | 'ready' | 'failed';

/** What a run produced, or how it failed to produce anything. */
export type SqlOutcome =
  | { ok: true; result: SqlRunResult }
  | { ok: false; notRun: 'timedOut' | 'unavailable'; message: string };

const HOST_URL = `${PREVIEW_ORIGIN}/sql-runner/index.html`;
/** host.js stops a run after 5 s; this only catches a host that never answers at all. */
const NO_ANSWER_MS = 90_000;

let frame: HTMLIFrameElement | null = null;
let frameLoaded = false;
let outbox: unknown[] = [];
const pending = new Map<string, { resolve: (o: SqlOutcome) => void; timer: number }>();

let status: SqlStatus = 'idle';
let statusDetail: string | undefined;
const listeners = new Set<() => void>();

function setStatus(next: SqlStatus, detail?: string) {
  status = next;
  statusDetail = detail;
  listeners.forEach((l) => l());
}

function send(msg: unknown) {
  if (frame?.contentWindow && frameLoaded) frame.contentWindow.postMessage(msg, PREVIEW_ORIGIN);
  else outbox.push(msg);
}

function onMessage(event: MessageEvent) {
  if (!frame || event.source !== frame.contentWindow || event.origin !== new URL(PREVIEW_ORIGIN).origin) return;
  const msg = event.data as { type?: string; id?: string; status?: SqlStatus; detail?: string; result?: SqlRunResult; timedOut?: boolean; failed?: string };
  if (msg?.type === 'cg:sql-status' && msg.status) {
    setStatus(msg.status, msg.detail ?? undefined);
  } else if (msg?.type === 'cg:sql-result' && msg.id) {
    const run = pending.get(msg.id);
    if (!run) return;
    pending.delete(msg.id);
    window.clearTimeout(run.timer);
    if (msg.result) run.resolve({ ok: true, result: msg.result });
    else if (msg.timedOut) {
      run.resolve({ ok: false, notRun: 'timedOut', message: 'Your SQL took too long (more than 5 seconds), so Codegrove stopped it. Is there a recursive query with nothing to stop it?' });
    } else run.resolve({ ok: false, notRun: 'unavailable', message: msg.failed ?? "SQL couldn't start. Reload the page and try again." });
  }
}

/** Creates the hidden runner iframe (once). Called on the first SQL lesson, before the learner needs it. */
export function preloadSql(): void {
  if (frame) return;
  window.addEventListener('message', onMessage);
  frame = document.createElement('iframe');
  frame.src = HOST_URL;
  // allow-same-origin gives the frame the PREVIEW origin (needed to start its worker), never the app's.
  frame.setAttribute('sandbox', 'allow-scripts allow-same-origin');
  frame.setAttribute('aria-hidden', 'true');
  frame.tabIndex = -1;
  frame.title = 'SQL runner';
  frame.hidden = true;
  frame.addEventListener('load', () => {
    frameLoaded = true;
    frame?.contentWindow?.postMessage({ type: 'cg:sql-hello' }, PREVIEW_ORIGIN);
    const queued = outbox;
    outbox = [];
    queued.forEach(send);
  });
  setStatus('loading');
  document.body.appendChild(frame);
}

/** Builds the database, runs the .sql files, then the tests. Never rejects. */
export function runSqlInBrowser(request: SqlRunRequest): Promise<SqlOutcome> {
  preloadSql();
  const id = crypto.randomUUID();
  return new Promise((resolve) => {
    pending.set(id, {
      resolve,
      timer: window.setTimeout(() => {
        pending.delete(id);
        resolve({ ok: false, notRun: 'unavailable', message: "SQL didn't answer. Check your internet connection, then reload the page." });
      }, NO_ANSWER_MS),
    });
    send({ type: 'cg:sql-run', id, request });
  });
}

/** The runner's state, for "Getting SQL ready…" messages. */
export function useSqlStatus(): { status: SqlStatus; detail?: string } {
  const current = useSyncExternalStore(
    (onChange) => {
      listeners.add(onChange);
      return () => listeners.delete(onChange);
    },
    () => `${status}|${statusDetail ?? ''}`,
  );
  const [s, ...rest] = current.split('|');
  return { status: s as SqlStatus, detail: rest.join('|') || undefined };
}

/** Test results for a run that couldn't check anything. */
export function failAllSql(tests: TestSpec[], reason: string): TestResult[] {
  return tests.map((t) => ({ testId: t.id, passed: false, message: `${t.failMessage} (${reason})` }));
}
