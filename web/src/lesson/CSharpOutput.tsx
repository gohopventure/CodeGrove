import { forwardRef, useCallback, useEffect, useImperativeHandle, useRef, useState } from 'react';
import type { Files } from '../api/types';
import {
  failAll, isWebProgram, runCSharp, useCSharpStatus,
  type CSharpDiagnostic, type CSharpHttpRequest, type CSharpRunResult, type CSharpServerInfo,
} from '../runner/csharp';
import type { ConsoleLine } from '../runner/types';
import type { LivePreviewHandle } from './LivePreview';
import { monaco } from './monacoSetup';
import { TryItPanel } from './TryItPanel';

/**
 * The Output panel for C# lessons: what the program printed, plus compile and runtime errors in plain
 * words. Same handle as LivePreview (`run(files, tests)`), so the lesson player treats both alike.
 *
 * C# doesn't re-run on every keystroke like a web page: compiling takes a moment, and a console program
 * "happens" once. The learner presses Run (or Check my code, which runs the tests too).
 *
 * A program that starts a web server (Web API course) also gets the Try it panel, for sending it requests.
 */
export const CSharpOutput = forwardRef<LivePreviewHandle, {
  /** The code the Run button runs. */
  files: Files;
  /** Run once as soon as the panel appears (the worked example). */
  autoRun?: boolean;
  title?: string;
}>(function CSharpOutput({ files, autoRun = false, title = 'Output' }, ref) {
  const { status, detail } = useCSharpStatus();
  const [lines, setLines] = useState<ConsoleLine[]>([]);
  const [last, setLast] = useState<CSharpRunResult | null>(null);
  const [running, setRunning] = useState(false);
  const [server, setServer] = useState<CSharpServerInfo | null>(null);
  const latest = useRef(0); // only the newest run may update the panel

  const execute = useCallback(async (code: Files, tests: Parameters<typeof runCSharp>[1] = [], requests?: CSharpHttpRequest[]) => {
    const ticket = ++latest.current;
    setRunning(true);
    const result = await runCSharp(code, tests, undefined, requests);
    if (ticket !== latest.current) return null;
    setRunning(false);
    setLast(result);
    setLines(result.console);
    if (result.compiled) setServer(result.server ?? null);
    showMarkers(code, result.diagnostics);
    return result;
  }, []);

  const web = isWebProgram(files);
  const filesKey = JSON.stringify(files);
  const sendRequests = useCallback(async (requests: CSharpHttpRequest[]) => {
    const result = await execute(files, [], requests);
    if (!result) return null;
    return result.responses ?? [];
  }, [execute, files]);

  useImperativeHandle(ref, () => ({
    async run(code, tests) {
      const result = await execute(code, tests);
      if (!result) return null; // replaced by a newer run
      // A program that ran too long fails every check; if C# couldn't run at all, nothing was checked.
      if (result.notRun === 'timedOut') return failAll(tests, 'Your program took too long, so it was stopped.');
      if (result.notRun === 'unavailable' || result.results.length !== tests.length) return null;
      return result.results;
    },
  }), [execute]);

  const autoRan = useRef(false);
  useEffect(() => {
    if (!autoRun || autoRan.current) return;
    autoRan.current = true;
    void execute(files);
  }, [autoRun, execute, files]);

  const empty = lines.length === 0;
  return (
    // A web server also shows the Try it panel; then the whole panel scrolls, so the console keeps a usable height.
    <div className={`flex h-full min-h-[320px] flex-col rounded-card border border-line bg-surface ${web ? 'overflow-auto' : 'overflow-hidden'}`}>
      <div className="flex items-center gap-2 border-b border-line bg-surface-2 px-3 py-1">
        <span className="text-sm font-bold">{title}</span>
        <button
          type="button"
          className="btn-secondary ml-auto px-3 py-0.5 text-sm"
          onClick={() => void execute(files)}
          disabled={running}
        >
          <span aria-hidden="true">▶ </span>{running ? 'Running…' : 'Run'}
        </button>
      </div>

      <div aria-live="polite" className={`${web ? 'min-h-28 shrink-0' : 'min-h-0'} flex-1 overflow-auto bg-[#1b2128] p-3 font-mono text-sm leading-relaxed text-[#e8edf2]`}>
        {status === 'loading' && (running || empty) && (
          <p role="status" className="flex items-center gap-2 text-[#9fb0c0]">
            <span className="inline-block h-4 w-4 animate-spin rounded-full border-2 border-[#4a5560] border-t-[#e8edf2]" aria-hidden="true" />
            Getting C# ready… The first time takes a little while.
          </p>
        )}
        {status === 'failed' && empty && <p className="text-[#ff9b9b]">✕ {detail ?? "C# couldn't start."}</p>}
        {empty && status !== 'loading' && status !== 'failed' && !running && (
          <p className="text-[#9fb0c0]">Press Run to see what your program prints.</p>
        )}
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
              {l.text === '' ? ' ' : l.text}
            </li>
          ))}
        </ol>
      </div>

      {web && <div className="shrink-0"><TryItPanel server={server} filesKey={filesKey} send={sendRequests} busy={running} /></div>}

      {last && (
        <p className="border-t border-line px-3 py-1 text-xs text-muted">
          {last.compiled
            ? `Built in ${formatMs(last.compileMs)}, ran in ${formatMs(last.runMs)}.`
            : last.diagnostics.some((d) => d.severity === 'error')
              ? 'Your code didn\'t build. The red underlines in the editor show where.'
              : ''}
        </p>
      )}
    </div>
  );
});

function formatMs(ms: number) {
  return ms < 1000 ? `${ms} ms` : `${(ms / 1000).toFixed(1)} s`;
}

/** Underlines compile errors and warnings in the editor (models are keyed by file name, see CodeEditor). */
function showMarkers(files: Files, diagnostics: CSharpDiagnostic[]) {
  for (const name of Object.keys(files).filter((f) => f.endsWith('.cs'))) {
    const model = monaco.editor.getModel(monaco.Uri.parse(name));
    if (!model) continue;
    monaco.editor.setModelMarkers(
      model,
      'csharp',
      diagnostics
        .filter((d) => d.file === name)
        .map((d) => ({
          severity: d.severity === 'error' ? monaco.MarkerSeverity.Error : monaco.MarkerSeverity.Warning,
          message: d.friendly ? `${d.friendly}\n${d.code}: ${d.message}` : `${d.code}: ${d.message}`,
          startLineNumber: d.line,
          startColumn: d.column,
          endLineNumber: d.endLine,
          // A zero-width error (a missing ;) still needs something to underline.
          endColumn: d.endLine === d.line && d.endColumn <= d.column ? d.column + 1 : d.endColumn,
        })),
    );
  }
}
