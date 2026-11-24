import { forwardRef, useCallback, useEffect, useId, useImperativeHandle, useMemo, useRef, useState } from 'react';
import type { Files } from '../api/types';
import { failAll, useCSharpStatus, type CSharpRunResult } from '../runner/csharp';
import { apiFiles, mergeResults, splitTests, webFiles } from '../runner/fullStack';
import { LiveApi } from '../runner/liveApi';
import type { ConsoleLine, TestSpec } from '../runner/types';
import { showMarkers } from './CSharpOutput';
import { LivePreview, type LivePreviewHandle } from './LivePreview';
import { NetworkPanel } from './NetworkPanel';

type Tab = 'preview' | 'server' | 'network';

/**
 * The output panel for FullStack challenges (the capstone): the learner's React page and the API it calls,
 * side by side in one app. Same handle as LivePreview (`run(files, tests)`), so the lesson player treats it
 * like the other panels.
 *
 * - ▶ Run (or Check my code) compiles the api/ files and starts the server, which stays running; then the
 *   page in web/ is rendered and its fetch('/api/…') calls go to that server (liveApi.ts).
 * - Editing a web/ file re-renders the page right away, against the same running server, so the rows
 *   it added are still there. Editing an api/ file needs ▶ Run: a note says so.
 * - Tabs: Preview (the page), Server (what the API printed, and its errors), Network (every request).
 *   The preview stays mounted while another tab is open, so the page doesn't reload.
 */
export const FullStackOutput = forwardRef<LivePreviewHandle, {
  files: Files;
  /** Start the API and show the page as soon as the panel appears. */
  autoRun?: boolean;
  title?: string;
  /** The editor's modelPrefix, so compile errors are underlined in the right editor. */
  modelPrefix?: string;
}>(function FullStackOutput({ files, autoRun = true, title = 'Your app', modelPrefix = '' }, ref) {
  const ids = useId();
  const { status, detail } = useCSharpStatus();
  const live = useMemo(() => new LiveApi(apiFiles), []);
  const preview = useRef<LivePreviewHandle>(null);
  const [tab, setTab] = useState<Tab>('preview');
  const [serverLines, setServerLines] = useState<ConsoleLine[]>([]);
  const [last, setLast] = useState<CSharpRunResult | null>(null);
  const [running, setRunning] = useState(false);
  const [startedApi, setStartedApi] = useState<string | null>(null); // the api/ files the server is running
  const latest = useRef(0);

  const apiKey = JSON.stringify(apiFiles(files));
  const webKey = JSON.stringify(webFiles(files));

  const execute = useCallback(async (code: Files, tests: TestSpec[] = []) => {
    const ticket = ++latest.current;
    setRunning(true);
    const sides = splitTests(tests);
    const result = await live.start(code, sides.api);
    if (ticket !== latest.current) return null;
    setLast(result);
    setServerLines(result.console);
    setStartedApi(JSON.stringify(apiFiles(code)));
    showMarkers(apiFiles(code), result.diagnostics, modelPrefix);
    // A server that didn't start is the thing to look at, not an empty page.
    if (!result.liveSession) setTab('server');

    const web = await preview.current?.run(webFiles(code), sides.web);
    if (ticket !== latest.current) return null;
    setRunning(false);

    let api = result.results;
    if (result.notRun === 'timedOut') api = failAll(sides.api, 'Your server took too long to start, so it was stopped.');
    else if (result.notRun === 'unavailable' || api.length !== sides.api.length) return null;
    return mergeResults(tests, api, web ?? null);
  }, [live, modelPrefix]);

  useImperativeHandle(ref, () => ({ run: (code, tests) => execute(code, tests) }), [execute]);

  // Start once when the panel appears.
  const autoRan = useRef(false);
  useEffect(() => {
    if (!autoRun || autoRan.current) return;
    autoRan.current = true;
    void execute(files);
  }, [autoRun, execute, files]);

  // Page edits show up right away, against the server that's already running.
  const shownWeb = useRef(webKey);
  useEffect(() => {
    if (startedApi === null || running || shownWeb.current === webKey) return;
    const t = window.setTimeout(() => {
      shownWeb.current = webKey;
      void preview.current?.run(JSON.parse(webKey) as Files, []);
    }, 400);
    return () => window.clearTimeout(t);
  }, [webKey, startedApi, running]);
  useEffect(() => {
    if (running) shownWeb.current = webKey; // a run renders the current page itself
  }, [running, webKey]);

  const apiChanged = startedApi !== null && startedApi !== apiKey;
  const serverProblem = serverLines.some((l) => l.level === 'error');
  const tabs: { id: Tab; label: string }[] = [
    { id: 'preview', label: 'Preview' },
    { id: 'server', label: serverProblem ? 'Server ⚠' : 'Server' },
    { id: 'network', label: 'Network' },
  ];

  return (
    <div className="flex h-full min-h-[320px] flex-col overflow-hidden rounded-card border border-line bg-surface">
      <div className="flex items-center gap-2 border-b border-line bg-surface-2 px-2 py-1">
        <span className="sr-only">{title}</span>
        <div role="tablist" aria-label={title} className="flex gap-1">
          {tabs.map((t) => (
            <button
              key={t.id}
              id={`${ids}-${t.id}-tab`}
              role="tab"
              type="button"
              aria-selected={tab === t.id}
              aria-controls={`${ids}-${t.id}`}
              onClick={() => setTab(t.id)}
              className={`rounded px-3 py-1 text-sm ${tab === t.id ? 'bg-surface font-bold text-ink' : 'text-muted'} ${t.id === 'server' && serverProblem ? 'text-bad' : ''}`}
            >
              {t.label}
            </button>
          ))}
        </div>
        <button
          type="button"
          className="btn-secondary ml-auto px-3 py-0.5 text-sm"
          onClick={() => void execute(files)}
          disabled={running}
        >
          <span aria-hidden="true">▶ </span>{running ? 'Starting…' : 'Run'}
        </button>
      </div>

      {apiChanged && !running && (
        <p role="status" className="border-b border-line bg-surface-2 px-3 py-1 text-sm">
          You changed your API code. Press <strong>▶ Run</strong> to restart your server with it.
        </p>
      )}

      <div id={`${ids}-preview`} role="tabpanel" aria-labelledby={`${ids}-preview-tab`} hidden={tab !== 'preview'} className="relative min-h-0 flex-1">
        <LivePreview ref={preview} title="Your app's page" liveApi={live.handle} showHeader={false} />
        {/* Until the API is up there's no page to show: say so instead of a blank white box. */}
        {running && startedApi === null && (
          <div className="absolute inset-0 flex items-center justify-center gap-3 bg-surface text-muted">
            <span className="inline-block h-5 w-5 animate-spin rounded-full border-2 border-line border-t-ink" aria-hidden="true" />
            <span>
              {status === 'loading' ? 'Getting C# ready…' : 'Starting your server…'}
            </span>
          </div>
        )}
      </div>

      <div id={`${ids}-server`} role="tabpanel" aria-labelledby={`${ids}-server-tab`} hidden={tab !== 'server'} className="min-h-0 flex-1 overflow-auto bg-[#1b2128] p-3 font-mono text-sm leading-relaxed text-[#e8edf2]">
        <ServerLog lines={serverLines} running={running} status={status} detail={detail} />
        {last && !last.compiled && last.diagnostics.some((d) => d.severity === 'error') && (
          <p className="mt-2 text-[#9fb0c0]">Your API code didn't build. The red underlines in the editor show where.</p>
        )}
      </div>

      <div id={`${ids}-network`} role="tabpanel" aria-labelledby={`${ids}-network-tab`} hidden={tab !== 'network'} className="min-h-0 flex-1 overflow-auto">
        <NetworkPanel api={live} showTitle={false} />
      </div>
    </div>
  );
});

function ServerLog({ lines, running, status, detail }: { lines: ConsoleLine[]; running: boolean; status: string; detail?: string }) {
  const empty = lines.length === 0;
  return (
    <div aria-live="polite">
      {status === 'loading' && (running || empty) && (
        <p role="status" className="flex items-center gap-2 text-[#9fb0c0]">
          <span className="inline-block h-4 w-4 animate-spin rounded-full border-2 border-[#4a5560] border-t-[#e8edf2]" aria-hidden="true" />
          Getting C# ready… The first time takes a little while.
        </p>
      )}
      {status === 'failed' && empty && <p className="text-[#ff9b9b]">✕ {detail ?? "C# couldn't start."}</p>}
      {empty && status === 'ready' && !running && <p className="text-[#9fb0c0]">Press Run to start your server.</p>}
      <ol>
        {lines.map((l, i) => (
          <li
            key={i}
            className={`whitespace-pre-wrap break-words ${l.level === 'error' ? 'text-[#ff9b9b]' : l.level === 'info' || l.level === 'warn' ? 'text-[#ffd479]' : ''}`}
          >
            {l.level === 'error' && <span aria-hidden="true">✕ </span>}
            {l.level === 'error' && <span className="sr-only">Error: </span>}
            {l.level === 'warn' && <span aria-hidden="true">⚠ </span>}
            {l.level === 'warn' && <span className="sr-only">Warning: </span>}
            {l.text === '' ? ' ' : l.text}
          </li>
        ))}
      </ol>
    </div>
  );
}
