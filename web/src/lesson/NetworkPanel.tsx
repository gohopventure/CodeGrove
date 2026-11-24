import { useId, useState } from 'react';
import { useNetworkLog, type LiveApi, type NetworkEntry } from '../runner/liveApi';

/**
 * The capstone's Network panel: every request the learner's page sent to their API, newest last, like the
 * Network tab in a browser's developer tools. Selecting one shows what was sent, what came back, and
 * what the server printed while answering (including a crash in plain words).
 *
 * Accessibility: a real table with a caption; each row's method and path is a button that opens its
 * details below the table. New rows are announced politely, by count, not one by one.
 */
export function NetworkPanel({ api }: { api: LiveApi }) {
  const entries = useNetworkLog(api);
  const [openId, setOpenId] = useState<number | null>(null);
  const ids = useId();
  const open = entries.find((e) => e.id === openId) ?? null;

  return (
    <section aria-labelledby={`${ids}-title`} className="flex min-h-0 flex-col text-sm">
      <div className="flex items-center justify-between border-b border-line bg-surface-2 px-3 py-1">
        <h3 id={`${ids}-title`} className="font-bold">
          Network
        </h3>
        {entries.length > 0 && (
          <button type="button" className="text-xs text-muted underline" onClick={() => { api.clearLog(); setOpenId(null); }}>
            Clear
          </button>
        )}
      </div>
      <p className="sr-only" aria-live="polite">
        {entries.length === 1 ? '1 request' : `${entries.length} requests`}
      </p>
      {entries.length === 0 ? (
        <p className="px-3 py-2 text-muted">No requests yet. When your page calls fetch('/api/…'), each request shows up here.</p>
      ) : (
        <div className="min-h-0 overflow-auto">
          <table className="w-full border-collapse font-mono text-xs">
            <caption className="sr-only">Requests your page sent to your API</caption>
            <thead>
              <tr className="text-left text-muted">
                <th scope="col" className="px-3 py-1 font-normal">Request</th>
                <th scope="col" className="px-2 py-1 font-normal">Status</th>
                <th scope="col" className="px-3 py-1 text-right font-normal">Time</th>
              </tr>
            </thead>
            <tbody>
              {entries.map((e) => (
                <tr key={e.id} className={`border-t border-line ${e.id === openId ? 'bg-surface-2' : ''}`}>
                  <td className="px-3 py-1">
                    <button
                      type="button"
                      className="text-left hover:underline"
                      aria-expanded={e.id === openId}
                      aria-controls={`${ids}-details`}
                      onClick={() => setOpenId(e.id === openId ? null : e.id)}
                    >
                      <span className="font-bold">{e.method}</span> {e.path}
                    </button>
                  </td>
                  <td className={`px-2 py-1 ${statusColor(e)}`}>{statusText(e)}</td>
                  <td className="px-3 py-1 text-right text-muted">{e.ms === null ? '…' : `${e.ms} ms`}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {open && <Details id={`${ids}-details`} entry={open} />}
        </div>
      )}
    </section>
  );
}

function Details({ id, entry }: { id: string; entry: NetworkEntry }) {
  return (
    <div id={id} className="space-y-2 border-t border-line px-3 py-2">
      {entry.requestBody !== null && (
        <div>
          <h4 className="font-bold">Sent</h4>
          <pre className="whitespace-pre-wrap break-words font-mono text-xs">{pretty(entry.requestBody)}</pre>
        </div>
      )}
      {entry.error ? (
        <p className="text-bad">{entry.error}</p>
      ) : (
        <div>
          <h4 className="font-bold">
            Answer: {entry.status} {entry.reason}
          </h4>
          <pre className="whitespace-pre-wrap break-words font-mono text-xs">
            {entry.responseBody === '' ? <span className="text-muted">(no body)</span> : pretty(entry.responseBody)}
          </pre>
        </div>
      )}
      {entry.serverLog.length > 0 && (
        <div>
          <h4 className="font-bold">Your server printed</h4>
          <ol className="font-mono text-xs">
            {entry.serverLog.map((l, i) => (
              <li key={i} className={l.level === 'error' ? 'text-bad' : ''}>
                {l.level === 'error' ? '✕ ' : '› '}
                {l.text}
              </li>
            ))}
          </ol>
        </div>
      )}
    </div>
  );
}

function statusText(e: NetworkEntry): string {
  if (e.error) return 'no answer';
  if (e.status === null) return 'waiting';
  return `${e.status} ${e.reason}`;
}

function statusColor(e: NetworkEntry): string {
  if (e.error || (e.status !== null && e.status >= 400)) return 'text-bad';
  if (e.status !== null && e.status < 300) return 'text-ok';
  return 'text-muted';
}

/** JSON indented for reading; anything else as it came. */
function pretty(text: string): string {
  try {
    return JSON.stringify(JSON.parse(text), null, 2);
  } catch {
    return text;
  }
}
