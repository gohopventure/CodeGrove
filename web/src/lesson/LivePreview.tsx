import { forwardRef, useCallback, useEffect, useImperativeHandle, useRef, useState } from 'react';
import { buildRunDocument, composeFiles, type Files } from '../runner/compose';
import type { ConsoleLine, RunnerMessage, TestResult, TestSpec } from '../runner/types';

/** Where runner.html lives. Must be a different origin from the app (see vite.config.ts). */
const PREVIEW_ORIGIN = import.meta.env.VITE_PREVIEW_ORIGIN ?? 'http://127.0.0.1:5173';
const RUN_TIMEOUT_MS = 4000;

export interface LivePreviewHandle {
  /** Renders the files and runs the tests. Resolves with results, or null if the page never finished. */
  run(files: Files, tests: TestSpec[]): Promise<TestResult[] | null>;
}

interface PendingRun {
  runId: string;
  html: string;
  resolve: (results: TestResult[] | null) => void;
  timer: number;
}

/**
 * Shows the learner's page in a sandboxed iframe and runs tests inside it.
 *
 * Security: sandbox="allow-scripts" (no allow-same-origin) gives the frame an opaque origin, so learner
 * code can't touch the app's cookies, storage or DOM. runner.html is served from a separate origin
 * and its CSP blocks all network access. Messages are accepted only from this exact frame's window
 * and only for the current runId.
 */
export const LivePreview = forwardRef<LivePreviewHandle, { title: string }>(function LivePreview({ title }, ref) {
  const frameRef = useRef<HTMLIFrameElement>(null);
  const pending = useRef<PendingRun | null>(null);
  // The run whose frame is on screen. Console output keeps arriving after tests finish
  // (click handlers, timers), so it's matched against this, not the pending run.
  const currentRunId = useRef<string | null>(null);
  const [frameKey, setFrameKey] = useState(0);
  const [consoleLines, setConsoleLines] = useState<ConsoleLine[]>([]);

  const finish = useCallback((results: TestResult[] | null) => {
    const run = pending.current;
    if (!run) return;
    window.clearTimeout(run.timer);
    pending.current = null;
    run.resolve(results);
  }, []);

  useEffect(() => {
    const onMessage = (event: MessageEvent) => {
      const frame = frameRef.current;
      if (!frame || event.source !== frame.contentWindow) return;
      const msg = event.data as RunnerMessage;
      const run = pending.current;

      if (msg?.type === 'cg:ready' && run) {
        frame.contentWindow?.postMessage({ type: 'cg:run', html: run.html }, '*');
      } else if (msg?.type === 'cg:console' && msg.runId === currentRunId.current) {
        setConsoleLines((lines) => [...lines.slice(-199), { level: msg.level, text: String(msg.text).slice(0, 2000) }]);
      } else if (msg?.type === 'cg:done' && run && msg.runId === run.runId) {
        finish(Array.isArray(msg.results) ? msg.results : null);
      }
    };
    window.addEventListener('message', onMessage);
    return () => window.removeEventListener('message', onMessage);
  }, [finish]);

  useImperativeHandle(ref, () => ({
    run(files, tests) {
      finish(null); // a newer run replaces any unfinished one
      const runId = crypto.randomUUID();
      currentRunId.current = runId;
      setConsoleLines([]);
      return new Promise((resolve) => {
        pending.current = {
          runId,
          html: buildRunDocument(composeFiles(files), runId, tests),
          resolve,
          timer: window.setTimeout(() => {
            setConsoleLines((l) => [...l, { level: 'error', text: 'Your page took too long to finish loading, so we stopped it.' }]);
            finish(null);
          }, RUN_TIMEOUT_MS),
        };
        setFrameKey((k) => k + 1); // a fresh frame per run: nothing leaks between runs
      });
    },
  }), [finish]);

  return (
    <div className="flex h-full min-h-[320px] flex-col overflow-hidden rounded-card border border-line bg-surface">
      <div className="border-b border-line bg-surface-2 px-3 py-1 text-sm font-bold">Preview</div>
      <iframe
        key={frameKey}
        ref={frameRef}
        title={title}
        src={`${PREVIEW_ORIGIN}/runner.html`}
        sandbox="allow-scripts"
        referrerPolicy="no-referrer"
        className="min-h-0 w-full flex-1 bg-white"
      />
      <details className="border-t border-line text-sm" open={consoleLines.length > 0}>
        <summary className="cursor-pointer px-3 py-1 font-bold">
          Console {consoleLines.length > 0 && <span className="text-muted">({consoleLines.length})</span>}
        </summary>
        <ol aria-live="polite" className="max-h-32 overflow-auto px-3 pb-2 font-mono text-xs">
          {consoleLines.length === 0 && <li className="text-muted">Nothing logged yet.</li>}
          {consoleLines.map((l, i) => (
            <li key={i} className={l.level === 'error' ? 'text-bad' : l.level === 'warn' ? 'text-accent' : ''}>
              {l.level === 'error' ? '✕ ' : '› '}
              {l.text}
            </li>
          ))}
        </ol>
      </details>
    </div>
  );
});
