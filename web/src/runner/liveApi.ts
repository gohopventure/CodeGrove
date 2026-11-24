/**
 * The capstone's live API, app side: starts the learner's server in the C# runner (a run with
 * keepServer), answers the preview's fetch('/api/…') calls with it, and keeps a log of every request for
 * the Network panel, so learners can watch their frontend and backend talk.
 *
 * Flow: preview frame (mockApi.ts, cg:fetch) → LivePreview → LiveApi.handle → sendToLiveServer
 * (csharp.ts, cg:cs-serve) → C# worker (LiveServer.cs) → the learner's endpoint → back the same way.
 * Nothing leaves the browser tab.
 */
import { useSyncExternalStore } from 'react';
import { runCSharp, sendToLiveServer, type CSharpHttpExchange, type CSharpRunResult, type LiveServerReply } from './csharp';
import type { LiveFetchMessage, LiveFetchReply } from './mockApi';
import type { ConsoleLine, TestSpec } from './types';

/** One request in the Network panel. */
export interface NetworkEntry {
  id: number;
  method: string;
  path: string;
  requestBody: string | null;
  /** Null while waiting, or when there was no answer at all (see `error`). */
  status: number | null;
  reason: string;
  contentType: string | null;
  responseBody: string;
  /** How long the server took, in milliseconds. */
  ms: number | null;
  /** What the endpoint printed, and any crash explained in plain words. */
  serverLog: ConsoleLine[];
  error?: string;
}

/** How many requests the Network panel keeps (a page that polls mustn't fill memory). */
const MAX_ENTRIES = 100;

export const NOT_RUNNING = "Your API isn't running yet. Press ▶ Run to start it, then the page can call it.";
export const NOT_STARTED = "Your API didn't start, so the page can't call it. The Output panel says why.";

export type LiveApiReply = Omit<LiveFetchReply, 'type' | 'id'>;

export class LiveApi {
  private session: string | null = null;
  private files: Record<string, string> | null = null;
  private starting: Promise<CSharpRunResult> | null = null;
  private entries: NetworkEntry[] = [];
  private nextId = 0;
  private readonly listeners = new Set<() => void>();

  constructor(
    /** Turns the lesson's files into the .cs files for the runner (for example, only the api/ folder). */
    private readonly pickServerFiles: (files: Record<string, string>) => Record<string, string> = (f) => f,
  ) {}

  /**
   * Compiles and runs the server files and keeps the server running. Resolves with the run's result
   * (diagnostics, console, test results) for the Output panel. Clears the Network log.
   */
  start(files: Record<string, string>, tests: TestSpec[] = []): Promise<CSharpRunResult> {
    this.entries = [];
    this.emit();
    return this.boot(files, tests);
  }

  private boot(files: Record<string, string>, tests: TestSpec[]): Promise<CSharpRunResult> {
    this.files = files;
    this.session = null;
    const run = runCSharp(this.pickServerFiles(files), tests, undefined, undefined, { keepServer: true }).then((result) => {
      if (this.starting === run) {
        this.session = result.liveSession ?? null;
        this.starting = null;
      }
      return result;
    });
    this.starting = run;
    return run;
  }

  /** Whether a server is running (false after a failed start). */
  get running(): boolean {
    return this.session !== null;
  }

  /** The preview's fetch handler (LivePreview's `liveApi` prop). */
  readonly handle = async (request: LiveFetchMessage): Promise<LiveApiReply> => {
    const entry: NetworkEntry = {
      id: ++this.nextId, method: request.method, path: request.path, requestBody: request.body,
      status: null, reason: '', contentType: null, responseBody: '', ms: null, serverLog: [],
    };
    this.entries = [...this.entries.slice(-(MAX_ENTRIES - 1)), entry];
    this.emit();

    const started = performance.now();
    if (this.starting) await this.starting;
    let reply: LiveServerReply;
    if (!this.session) {
      // Never started, or the last start failed (a compile error): nothing to restart until the next Run.
      reply = { found: false, console: [{ level: 'error', text: this.files ? NOT_STARTED : NOT_RUNNING }] };
    } else {
      reply = await this.send(this.session, request);
      // The runner restarted (a time limit, or recycling) and the server went with it: start it again
      // from the same files and try once more. Its in-memory database starts over, as after any restart.
      if (!reply.found && !reply.timedOut && this.files) {
        await this.boot(this.files, []);
        if (this.session) reply = await this.send(this.session, request);
      }
    }
    const ms = Math.round(performance.now() - started);

    if (!reply.found || !reply.exchange) {
      const error = reply.console.find((l) => l.level === 'error')?.text ?? (this.files ? NOT_STARTED : NOT_RUNNING);
      this.update(entry.id, { ms, error, serverLog: reply.console });
      return { error };
    }
    this.update(entry.id, exchangeFields(reply.exchange, ms, reply.console));
    const { status, reason, headers, body } = reply.exchange;
    return { status, statusText: reason, headers, body };
  };

  private send(session: string, request: LiveFetchMessage): Promise<LiveServerReply> {
    return sendToLiveServer(session, { method: request.method, path: request.path, body: request.body ?? undefined, headers: request.headers });
  }

  /** The Network panel's entries, oldest first. */
  getEntries = (): NetworkEntry[] => this.entries;

  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  clearLog(): void {
    this.entries = [];
    this.emit();
  }

  private update(id: number, changes: Partial<NetworkEntry>) {
    this.entries = this.entries.map((e) => (e.id === id ? { ...e, ...changes } : e));
    this.emit();
  }

  private emit() {
    this.listeners.forEach((l) => l());
  }
}

function exchangeFields(x: CSharpHttpExchange, ms: number, serverLog: ConsoleLine[]): Partial<NetworkEntry> {
  return { status: x.status, reason: x.reason, contentType: x.contentType, responseBody: x.body, ms, serverLog };
}

/** The Network panel's entries, re-rendering when they change. */
export function useNetworkLog(api: LiveApi): NetworkEntry[] {
  return useSyncExternalStore(api.subscribe, api.getEntries);
}
