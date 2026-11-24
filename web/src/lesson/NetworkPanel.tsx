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
export function NetworkPanel({ api, showTitle = true }: {
  api: LiveApi;
  /** False inside a tab already called "Network" (the title stays for screen readers). */
  showTitle?: boolean;
}) {
  const [sendOpen, setSendOpen] = useState(false);
  const entries = useNetworkLog(api);
  const [openId, setOpenId] = useState<number | null>(null);
  const ids = useId();
  const open = entries.find((e) => e.id === openId) ?? null;

  return (
    <section aria-labelledby={`${ids}-title`} className="flex min-h-0 flex-col text-sm">
      <div className={`flex items-center justify-between px-3 py-1 ${showTitle ? 'border-b border-line bg-surface-2' : ''}`}>
        <h3 id={`${ids}-title`} className={showTitle ? 'font-bold' : 'sr-only'}>
          Network
        </h3>
        <div className="flex gap-3">
          <button type="button" className="text-xs text-muted underline" aria-expanded={sendOpen} onClick={() => setSendOpen((o) => !o)}>
            {sendOpen ? 'Hide send box' : 'Send a request'}
          </button>
          {entries.length > 0 && (
            <button type="button" className="text-xs text-muted underline" onClick={() => { api.clearLog(); setOpenId(null); }}>
              Clear
            </button>
          )}
        </div>
      </div>
      {sendOpen && <SendBox api={api} />}
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

/**
 * Sends one request to the learner's API from here, like Try it in the Web API course or a REST client:
 * handy for endpoints the page doesn't call yet. It goes through the same live server, so it shows up in
 * the list and changes the same database the page sees.
 */
function SendBox({ api }: { api: LiveApi }) {
  const ids = useId();
  const [method, setMethod] = useState('GET');
  const [path, setPath] = useState('/api/');
  const [body, setBody] = useState('');
  const [sending, setSending] = useState(false);
  const hasBody = method === 'POST' || method === 'PUT' || method === 'PATCH';
  const validPath = path.startsWith('/');

  async function send(e: React.FormEvent) {
    e.preventDefault();
    if (!validPath) return;
    setSending(true);
    await api.handle({
      type: 'cg:fetch', runId: '', id: '', method, path,
      headers: hasBody ? { 'content-type': 'application/json' } : {},
      body: hasBody ? body : null,
    });
    setSending(false);
  }

  return (
    <form onSubmit={send} className="space-y-2 border-b border-line px-3 py-2">
      <div className="flex gap-2">
        <label htmlFor={`${ids}-method`} className="sr-only">Method</label>
        <select id={`${ids}-method`} value={method} onChange={(e) => setMethod(e.target.value)} className="rounded border border-line bg-surface px-1 font-mono text-xs">
          {['GET', 'POST', 'PUT', 'PATCH', 'DELETE'].map((m) => <option key={m}>{m}</option>)}
        </select>
        <label htmlFor={`${ids}-path`} className="sr-only">Path</label>
        <input
          id={`${ids}-path`}
          value={path}
          onChange={(e) => setPath(e.target.value)}
          spellCheck={false}
          aria-invalid={!validPath}
          className="min-w-0 flex-1 rounded border border-line bg-surface px-2 font-mono text-xs"
        />
        <button type="submit" className="btn-secondary px-3 py-0.5 text-xs" disabled={sending || !validPath}>
          {sending ? 'Sending…' : 'Send'}
        </button>
      </div>
      {!validPath && <p className="text-xs text-bad">Start the path with a slash, like /api/matches.</p>}
      {hasBody && (
        <div>
          <label htmlFor={`${ids}-body`} className="text-xs text-muted">JSON body</label>
          <textarea
            id={`${ids}-body`}
            value={body}
            onChange={(e) => setBody(e.target.value)}
            rows={4}
            spellCheck={false}
            placeholder='{ "name": "…" }'
            className="block w-full rounded border border-line bg-surface p-2 font-mono text-xs"
          />
        </div>
      )}
    </form>
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
