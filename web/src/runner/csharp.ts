/**
 * Runs learner C# in the browser. The app keeps ONE hidden iframe for the whole session:
 * PREVIEW_ORIGIN/csharp-runner/index.html, which starts a Web Worker running .NET on WebAssembly with
 * Roslyn (src/Codegrove.CSharpRunner.Wasm). The first C# lesson downloads it (~6 MB compressed, then
 * cached); after that a run takes well under a second. Compiling, running and checking all happen in
 * the worker, on the preview origin, with no network access. See public/csharp-runner/index.html.
 */
import { useSyncExternalStore } from 'react';
import { PREVIEW_ORIGIN } from './origin';
import type { ConsoleLine, TestResult, TestSpec } from './types';

/** A compiler error or warning (Contracts.cs → Diagnostic). Lines and columns start at 1. */
export interface CSharpDiagnostic {
  file: string;
  line: number;
  column: number;
  endLine: number;
  endColumn: number;
  code: string;
  severity: 'error' | 'warning';
  /** The compiler's own words. */
  message: string;
  /** Plain-language version for beginners, or '' if there isn't one. */
  friendly: string;
}

/** A request for the learner's web server (Contracts.cs → HttpRequestSpec). A body is sent as JSON. */
export interface CSharpHttpRequest {
  method: string;
  path: string;
  body?: string;
  headers?: Record<string, string>;
}

/** What the learner's server answered (Contracts.cs → HttpExchange). */
export interface CSharpHttpExchange {
  method: string;
  path: string;
  status: number;
  /** "Not Found" for 404. */
  reason: string;
  contentType: string | null;
  body: string;
  headers: Record<string, string>;
}

/** The web server a program started, if it built one (Contracts.cs → ServerInfo). */
export interface CSharpServerInfo {
  running: boolean;
  url: string;
  /** "GET /pets/{id:int}" */
  endpoints: string[];
}

export type CSharpStopReason = 'None' | 'CompileErrors' | 'Exception' | 'TooManySteps' | 'TooDeep' | 'TooMuchOutput';

/** What a run produced (Contracts.cs → RunResult), or how it failed to produce anything. */
export interface CSharpRunResult {
  compiled: boolean;
  diagnostics: CSharpDiagnostic[];
  /** Ready for the Output panel: program output, compile errors and runtime errors, in order. */
  console: ConsoleLine[];
  output: string;
  stopped: CSharpStopReason;
  results: TestResult[];
  compileMs: number;
  runMs: number;
  /** Set when the program built a web app (the Web API course). */
  server?: CSharpServerInfo | null;
  /** Answers to the requests passed to runCSharp, in order. */
  responses?: CSharpHttpExchange[] | null;
  /** Set when `keepServer` was asked for and the server started: pass it to sendToLiveServer. */
  liveSession?: string | null;
  /** Set by this module when the run produced nothing: stopped after the time limit, or C# couldn't run. */
  notRun?: 'timedOut' | 'unavailable';
}

export type CSharpStatus = 'idle' | 'loading' | 'ready' | 'failed';

const HOST_URL = `${PREVIEW_ORIGIN}/csharp-runner/index.html`;
/** host.js stops a run after 15 s; this only catches a host that never answers at all. */
const NO_ANSWER_MS = 240_000;

interface Pending {
  resolve: (result: CSharpRunResult) => void;
  timer: number;
}

/**
 * The live server's answer (Contracts.cs → LiveResponse), or why there isn't one. `found: false` means
 * that server isn't running any more (a newer run replaced it, or the runner restarted).
 */
export type LiveServerReply =
  | { found: true; exchange: CSharpHttpExchange | null; console: ConsoleLine[] }
  | { found: false; console: ConsoleLine[]; timedOut?: boolean };

let frame: HTMLIFrameElement | null = null;
let frameLoaded = false;
let outbox: unknown[] = [];
const pending = new Map<string, Pending>();
const pendingServes = new Map<string, { resolve: (reply: LiveServerReply) => void; timer: number }>();

let status: CSharpStatus = 'idle';
let statusDetail: string | undefined;
const listeners = new Set<() => void>();

function setStatus(next: CSharpStatus, detail?: string) {
  status = next;
  statusDetail = detail;
  listeners.forEach((l) => l());
}

function send(msg: unknown) {
  if (frame?.contentWindow && frameLoaded) frame.contentWindow.postMessage(msg, PREVIEW_ORIGIN);
  else outbox.push(msg);
}

/** A result for runs that never got to the compiler, so callers always get the same shape. */
function problem(notRun: CSharpRunResult['notRun'], text: string): CSharpRunResult {
  return { compiled: false, diagnostics: [], console: [{ level: 'error', text }], output: '', stopped: 'Exception', results: [], compileMs: 0, runMs: 0, notRun };
}

function onMessage(event: MessageEvent) {
  if (!frame || event.source !== frame.contentWindow || event.origin !== new URL(PREVIEW_ORIGIN).origin) return;
  const msg = event.data as {
    type?: string; id?: string; status?: CSharpStatus; detail?: string;
    result?: CSharpRunResult; timedOut?: boolean; failed?: string; response?: LiveServerReply;
  };
  if (msg?.type === 'cg:cs-served' && msg.id) {
    const serve = pendingServes.get(msg.id);
    if (!serve) return;
    pendingServes.delete(msg.id);
    window.clearTimeout(serve.timer);
    if (msg.response) serve.resolve(msg.response);
    else if (msg.timedOut) {
      serve.resolve({
        found: false,
        timedOut: true,
        console: [{ level: 'error', text: 'Your server took more than 15 seconds to answer, so Codegrove stopped it. Is there a loop in that endpoint that never ends?' }],
      });
    } else serve.resolve({ found: false, console: [{ level: 'error', text: msg.failed ?? "C# couldn't start. Reload the page and try again." }] });
    return;
  }
  if (msg?.type === 'cg:cs-status' && msg.status) {
    setStatus(msg.status, msg.detail ?? undefined);
  } else if (msg?.type === 'cg:cs-result' && msg.id) {
    const run = pending.get(msg.id);
    if (!run) return;
    pending.delete(msg.id);
    window.clearTimeout(run.timer);
    if (msg.result) run.resolve(msg.result);
    else if (msg.timedOut) {
      run.resolve(problem('timedOut', 'Your program took too long (more than 15 seconds), so Codegrove stopped it. Is there a loop or a calculation that never ends?'));
    } else run.resolve(problem('unavailable', msg.failed ?? "C# couldn't start. Reload the page and try again."));
  }
}

/** Creates the hidden runner iframe (once). Called on the first C# lesson, before the learner needs it. */
export function preloadCSharp(): void {
  if (frame) return;
  window.addEventListener('message', onMessage);
  frame = document.createElement('iframe');
  frame.src = HOST_URL;
  // allow-same-origin gives the frame the PREVIEW origin (needed to start its worker), never the app's.
  frame.setAttribute('sandbox', 'allow-scripts allow-same-origin');
  frame.setAttribute('aria-hidden', 'true');
  frame.tabIndex = -1;
  frame.title = 'C# runner';
  frame.hidden = true;
  frame.addEventListener('load', () => {
    frameLoaded = true;
    frame?.contentWindow?.postMessage({ type: 'cg:cs-hello' }, PREVIEW_ORIGIN);
    const queued = outbox;
    outbox = [];
    queued.forEach(send);
  });
  setStatus('loading');
  document.body.appendChild(frame);
}

/**
 * Compiles and runs the .cs files, then the tests. Never rejects: problems come back as console lines.
 * `requests` are sent, in order, to the web server the program starts (if it starts one).
 */
export function runCSharp(
  files: Record<string, string>,
  tests: TestSpec[] = [],
  input?: string,
  requests?: CSharpHttpRequest[],
  options: { keepServer?: boolean } = {},
): Promise<CSharpRunResult> {
  preloadCSharp();
  const id = crypto.randomUUID();
  return new Promise((resolve) => {
    pending.set(id, {
      resolve,
      timer: window.setTimeout(() => {
        pending.delete(id);
        resolve(problem('unavailable', "C# didn't answer. Check your internet connection, then reload the page."));
      }, NO_ANSWER_MS),
    });
    send({ type: 'cg:cs-run', id, request: { files, tests, input, requests, keepServer: options.keepServer ?? false } });
  });
}

/**
 * Sends one request to the server a `keepServer` run left running (the capstone's preview calling its
 * API). Never rejects. Requests wait in the same queue as runs, so they're answered in order.
 */
export function sendToLiveServer(session: string, request: CSharpHttpRequest): Promise<LiveServerReply> {
  preloadCSharp();
  const id = crypto.randomUUID();
  return new Promise((resolve) => {
    pendingServes.set(id, {
      resolve,
      timer: window.setTimeout(() => {
        pendingServes.delete(id);
        resolve({ found: false, console: [{ level: 'error', text: "C# didn't answer. Check your internet connection, then reload the page." }] });
      }, NO_ANSWER_MS),
    });
    send({ type: 'cg:cs-serve', id, request: { session, request } });
  });
}

/** The runner's state, for "Getting C# ready…" messages. */
export function useCSharpStatus(): { status: CSharpStatus; detail?: string } {
  const current = useSyncExternalStore(
    (onChange) => {
      listeners.add(onChange);
      return () => listeners.delete(onChange);
    },
    () => `${status}|${statusDetail ?? ''}`,
  );
  const [s, ...rest] = current.split('|');
  return { status: s as CSharpStatus, detail: rest.join('|') || undefined };
}

/** Test results for a run that couldn't check anything (every test fails with the same reason). */
export function failAll(tests: TestSpec[], reason: string): TestResult[] {
  return tests.map((t) => ({ testId: t.id, passed: false, message: `${t.failMessage} (${reason})` }));
}

/** Whether these files are a web server (Web API course): the same rule as CSharpCodeRunner.IsWebProgram. */
export function isWebProgram(files: Record<string, string>): boolean {
  return Object.entries(files).some(([name, code]) => name.endsWith('.cs') && code.includes('WebApplication'));
}
