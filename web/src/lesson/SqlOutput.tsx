import { forwardRef, useCallback, useEffect, useImperativeHandle, useRef, useState } from 'react';
import type { Files } from '../api/types';
import { failAllSql, runSqlInBrowser, useSqlStatus } from '../runner/sql';
import type { SqlCell, SqlResultSet, SqlRunResult, SqlTable } from '../runner/sqlEngine';
import type { ConsoleLine } from '../runner/types';
import type { LivePreviewHandle } from './LivePreview';
import { monaco } from './monacoSetup';

type Tab = 'results' | 'tables';

/**
 * The Output panel for SQL lessons, with two tabs:
 * - Results: the rows each SELECT returned, what other statements did ("INSERT added 1 row."), and
 *   errors in plain words.
 * - Tables: every table in the database and its rows, so learners can see what they're querying
 *   and, after an INSERT or UPDATE, what changed. Before the first run it shows the starting database.
 *
 * Same handle as LivePreview (`run(files, tests)`), so the lesson player treats it like the others. Like
 * C#, SQL runs when the learner presses Run (or Check my code), not on every keystroke: an UPDATE
 * that runs while you're half-way through typing its WHERE would be confusing.
 */
export const SqlOutput = forwardRef<LivePreviewHandle, {
  /** The hidden starting database (the challenge's .setup.sql). */
  setup: string;
  /** The code the Run button runs. */
  files: Files;
  /** Run once as soon as the panel appears (the worked example). */
  autoRun?: boolean;
  title?: string;
}>(function SqlOutput({ setup, files, autoRun = false, title = 'Output' }, ref) {
  const { status, detail } = useSqlStatus();
  const [tab, setTab] = useState<Tab>('results');
  const [last, setLast] = useState<SqlRunResult | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const [tables, setTables] = useState<SqlTable[]>([]);
  const [running, setRunning] = useState(false);
  const latest = useRef(0); // only the newest run may update the panel

  const execute = useCallback(async (code: Files, tests: Parameters<LivePreviewHandle['run']>[1] = []) => {
    const ticket = ++latest.current;
    setRunning(true);
    const outcome = await runSqlInBrowser({ setup, files: code, tests });
    if (ticket !== latest.current) return null;
    setRunning(false);
    if (outcome.ok) {
      setLast(outcome.result);
      setTables(outcome.result.tables);
      setProblem(null);
      showMarkers(code, outcome.result);
    } else {
      setLast(null);
      setProblem(outcome.message);
    }
    return outcome;
  }, [setup]);

  useImperativeHandle(ref, () => ({
    async run(code, tests) {
      setTab('results');
      const outcome = await execute(code, tests);
      if (!outcome) return null; // replaced by a newer run
      if (!outcome.ok) return outcome.notRun === 'timedOut' ? failAllSql(tests, 'Your SQL took too long, so it was stopped.') : null;
      return outcome.result.results.length === tests.length ? outcome.result.results : null;
    },
  }), [execute]);

  // The example runs itself. A challenge only loads the starting database, so the Tables tab has
  // something to show before the learner's first run.
  const started = useRef(false);
  useEffect(() => {
    if (started.current) return;
    started.current = true;
    if (autoRun) {
      void execute(files);
    } else {
      void runSqlInBrowser({ setup, files: {} }).then((o) => {
        if (o.ok && latest.current === 0) setTables(o.result.tables);
      });
    }
  }, [autoRun, execute, files, setup]);

  return (
    <div className="flex h-full min-h-[320px] flex-col overflow-hidden rounded-card border border-line bg-surface">
      <div className="flex items-center gap-1 border-b border-line bg-surface-2 px-2 py-1">
        <span className="mr-2 px-1 text-sm font-bold">{title}</span>
        <div role="tablist" aria-label={`${title} view`} className="flex gap-1">
          {(['results', 'tables'] as const).map((t) => (
            <button
              key={t}
              type="button"
              role="tab"
              id={`sql-tab-${t}`}
              aria-selected={tab === t}
              aria-controls={`sql-panel-${t}`}
              className={`rounded px-2 py-0.5 text-sm ${tab === t ? 'bg-surface font-bold shadow-sm' : 'text-muted hover:text-ink'}`}
              onClick={() => setTab(t)}
            >
              {t === 'results' ? 'Results' : `Tables${tables.length ? ` (${tables.length})` : ''}`}
            </button>
          ))}
        </div>
        <button
          type="button"
          className="btn-secondary ml-auto px-3 py-0.5 text-sm"
          onClick={() => {
            setTab('results');
            void execute(files);
          }}
          disabled={running}
        >
          <span aria-hidden="true">▶ </span>{running ? 'Running…' : 'Run'}
        </button>
      </div>

      <div
        role="tabpanel"
        id={`sql-panel-${tab}`}
        aria-labelledby={`sql-tab-${tab}`}
        aria-live={tab === 'results' ? 'polite' : undefined}
        className="min-h-0 flex-1 overflow-auto bg-[#1b2128] p-3 font-mono text-sm leading-relaxed text-[#e8edf2]"
      >
        {status === 'loading' && running && (
          <p role="status" className="flex items-center gap-2 text-[#9fb0c0]">
            <span className="inline-block h-4 w-4 animate-spin rounded-full border-2 border-[#4a5560] border-t-[#e8edf2]" aria-hidden="true" />
            Getting SQL ready…
          </p>
        )}
        {status === 'failed' && !last && <p className="text-[#ff9b9b]">✕ {detail ?? "SQL couldn't start."}</p>}
        {tab === 'results' ? <Results last={last} problem={problem} running={running} /> : <Tables tables={tables} />}
      </div>

      {last && tab === 'results' && (
        <p className="border-t border-line px-3 py-1 text-xs text-muted">
          {last.error ? 'Your SQL stopped at an error. The red underline in the editor shows where.' : `Ran in ${last.runMs} ms.`}
        </p>
      )}
    </div>
  );
});

function Results({ last, problem, running }: { last: SqlRunResult | null; problem: string | null; running: boolean }) {
  if (problem) return <p className="text-[#ff9b9b]">✕ {problem}</p>;
  if (!last) return running ? null : <p className="text-[#9fb0c0]">Press Run to see what your SQL returns. The Tables tab shows the database.</p>;
  // Rows first (what most lessons are about), then what the other statements did, then any error.
  return (
    <div className="space-y-4">
      {last.resultSets.map((set, i) => <ResultGrid key={i} set={set} />)}
      {last.console.length > 0 && (
        <ol className="space-y-1">
          {last.console.map((l: ConsoleLine, i) => (
            <li key={i} className={`whitespace-pre-wrap break-words ${l.level === 'error' ? 'text-[#ff9b9b]' : 'text-[#9fe0b0]'}`}>
              {l.level === 'error' ? <><span aria-hidden="true">✕ </span><span className="sr-only">Error: </span></> : <span aria-hidden="true">✓ </span>}
              {l.text}
            </li>
          ))}
        </ol>
      )}
    </div>
  );
}

function ResultGrid({ set }: { set: SqlResultSet }) {
  const count = `${set.rows.length}${set.truncated ? '+' : ''} ${set.rows.length === 1 ? 'row' : 'rows'}`;
  return (
    <Grid
      caption={<>{set.file}, line {set.line} <span className="text-[#9fb0c0]">· {count}</span></>}
      columns={set.columns.map((name) => ({ name }))}
      rows={set.rows}
      footer={set.truncated ? `Only the first ${set.rows.length} rows are shown.` : set.rows.length === 0 ? 'No rows matched.' : undefined}
    />
  );
}

function Tables({ tables }: { tables: SqlTable[] }) {
  if (tables.length === 0) return <p className="text-[#9fb0c0]">There are no tables yet.</p>;
  return (
    <div className="space-y-4">
      {tables.map((t) => (
        <Grid
          key={t.name}
          caption={<>{t.name} <span className="text-[#9fb0c0]">· {t.rowCount} {t.rowCount === 1 ? 'row' : 'rows'}</span></>}
          columns={t.columns.map((c) => ({ name: c.name, note: [c.primaryKey ? 'key' : '', c.type.toLowerCase()].filter(Boolean).join(', ') }))}
          rows={t.rows}
          footer={t.rows.length < t.rowCount ? `Showing the first ${t.rows.length} rows.` : t.rowCount === 0 ? 'This table is empty.' : undefined}
        />
      ))}
    </div>
  );
}

function Grid({ caption, columns, rows, footer }: {
  caption: React.ReactNode;
  columns: { name: string; note?: string }[];
  rows: SqlCell[][];
  footer?: string;
}) {
  return (
    <div>
      <div className="overflow-x-auto">
        <table className="border-collapse text-left">
          <caption className="mb-1 text-left font-bold">{caption}</caption>
          <thead>
            <tr>
              {columns.map((c, i) => (
                <th key={i} scope="col" className="border border-[#3a4550] bg-[#26303a] px-2 py-0.5 font-bold">
                  {c.name}
                  {c.note && <span className="block text-xs font-normal text-[#9fb0c0]">{c.note}</span>}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {rows.map((row, r) => (
              <tr key={r}>
                {row.map((v, c) => (
                  <td key={c} className={`border border-[#3a4550] px-2 py-0.5 ${typeof v === 'number' ? 'text-right' : ''} ${v === null ? 'italic text-[#9fb0c0]' : ''}`}>
                    {v === null ? 'NULL' : String(v)}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {footer && <p className="mt-1 text-xs text-[#9fb0c0]">{footer}</p>}
    </div>
  );
}

/** Underlines the error in the editor (models are keyed by file name, see CodeEditor). */
function showMarkers(files: Files, result: SqlRunResult) {
  for (const name of Object.keys(files).filter((f) => f.endsWith('.sql') && !f.startsWith('.'))) {
    const model = monaco.editor.getModel(monaco.Uri.parse(name));
    if (!model) continue;
    const err = result.error?.file === name ? result.error : null;
    monaco.editor.setModelMarkers(model, 'sql', err ? [{
      severity: monaco.MarkerSeverity.Error,
      message: err.friendly ? `${err.friendly}\n${err.message}` : err.message,
      startLineNumber: err.line,
      startColumn: model.getLineFirstNonWhitespaceColumn(err.line) || 1,
      endLineNumber: err.line,
      endColumn: model.getLineMaxColumn(err.line),
    }] : []);
  }
}
