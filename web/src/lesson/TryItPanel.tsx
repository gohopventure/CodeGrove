import { useEffect, useId, useRef, useState } from 'react';
import type { CSharpHttpExchange, CSharpHttpRequest, CSharpServerInfo } from '../runner/csharp';

/**
 * "Try it": a kid-sized request tool (think Postman) for the Web API course. Pick a method, type a path,
 * maybe a JSON body, press Send, and see what the learner's server answered.
 *
 * The server isn't kept alive between sends: every Send runs the program again and replays the requests
 * sent since the code last changed, then the new one. So a POST followed by a GET finds what was posted,
 * just as it would on a real running server, and changing the code restarts with fresh data (as a real
 * restart would).
 *
 * Paths from the endpoint buttons keep their route blanks, like /pets/{id}. Sending one as-is would put the
 * text "{id}" in the blank, so the blank is selected (typing replaces it), a note says what to do, and Send
 * waits until it's filled in.
 */
export function TryItPanel({ server, filesKey, send, busy }: {
  /** From the last run; null until the program has run once. */
  server: CSharpServerInfo | null;
  /** Changes whenever the code changes (the server "restarts"). */
  filesKey: string;
  /** Runs the program with these requests and returns the answers (null if the run was replaced or failed). */
  send: (requests: CSharpHttpRequest[]) => Promise<CSharpHttpExchange[] | null>;
  busy: boolean;
}) {
  const ids = useId();
  const [method, setMethod] = useState('GET');
  const [path, setPath] = useState(() => firstPath(server) ?? '/');
  const [body, setBody] = useState('');
  const [history, setHistory] = useState<CSharpHttpRequest[]>([]);
  const [answer, setAnswer] = useState<CSharpHttpExchange | null>(null);
  const [note, setNote] = useState<string | null>(null);
  const lastKey = useRef(filesKey);
  const pathInput = useRef<HTMLInputElement>(null);

  /** Focuses the path and selects its first {blank}, so typing replaces it. */
  function selectBlank(p: string) {
    const blank = findBlank(p);
    if (!blank) return;
    requestAnimationFrame(() => {
      const input = pathInput.current;
      if (!input) return;
      input.focus();
      input.setSelectionRange(blank.start, blank.end);
    });
  }

  const blank = findBlank(path);
  const fillWith = blank && blankIsNumber(server, blank.name) ? 'a whole number' : 'a value';

  useEffect(() => {
    if (lastKey.current === filesKey) return;
    lastKey.current = filesKey;
    if (history.length > 0) setNote('Your code changed, so the server restarted with its starting data.');
    setHistory([]);
  }, [filesKey, history.length]);

  // Start on the server's first endpoint once it's known, rather than a bare "/".
  const firstEndpoint = firstPath(server);
  useEffect(() => {
    if (firstEndpoint) setPath((p) => (p === '/' ? firstEndpoint : p));
  }, [firstEndpoint]);

  const hasBody = method !== 'GET' && method !== 'DELETE';

  async function onSend(event: React.FormEvent) {
    event.preventDefault();
    if (blank) {
      // The hint under the path already says what to do (and is read out with it): just go back to the blank.
      setNote(null);
      selectBlank(path);
      return;
    }
    let request: CSharpHttpRequest = { method, path: path.trim() || '/' };
    if (hasBody && body.trim()) {
      try {
        JSON.parse(body);
      } catch {
        setNote('The body isn\'t valid JSON yet. Check the quotes, commas and curly braces.');
        return;
      }
      request = { ...request, body };
    }
    setNote(null);
    const answers = await send([...history, request]);
    if (!answers) return;
    const last = answers.at(-1);
    if (!last) {
      setAnswer(null);
      setNote("Your server isn't running, so nothing answered. The Output panel says why.");
      return;
    }
    setAnswer(last);
    setHistory((h) => [...h, request]);
  }

  return (
    <section aria-labelledby={`${ids}-title`} className="border-t border-line bg-surface">
      <form onSubmit={onSend} className="flex flex-wrap items-end gap-2 px-3 py-2">
        <h3 id={`${ids}-title`} className="w-full text-sm font-bold">Try it: send your server a request</h3>
        <label className="flex flex-col text-xs font-bold text-muted">
          Method
          <select className="input mt-1 w-28 py-1 font-mono text-sm text-ink" value={method} onChange={(e) => setMethod(e.target.value)}>
            {['GET', 'POST', 'PUT', 'PATCH', 'DELETE'].map((m) => <option key={m}>{m}</option>)}
          </select>
        </label>
        <label className="flex min-w-40 flex-1 flex-col text-xs font-bold text-muted">
          Path
          <input
            ref={pathInput}
            className="input mt-1 py-1 font-mono text-sm text-ink"
            value={path}
            onChange={(e) => setPath(e.target.value)}
            aria-describedby={blank ? `${ids}-blank` : undefined}
            spellCheck={false}
            autoComplete="off"
          />
        </label>
        <button type="submit" className="btn-primary px-4 py-1 text-sm" disabled={busy}>
          {busy ? 'Sending…' : 'Send'}
        </button>
        {blank && (
          <p id={`${ids}-blank`} className="w-full text-xs text-muted">
            Replace <code className="font-mono text-ink">{`{${blank.name}}`}</code> in the path with {fillWith}, then press Send.
          </p>
        )}
        {hasBody && (
          <label className="flex w-full flex-col text-xs font-bold text-muted">
            Body (JSON)
            <textarea
              className="input mt-1 min-h-16 py-1 font-mono text-sm text-ink"
              value={body}
              onChange={(e) => setBody(e.target.value)}
              placeholder={'{ "name": "Moss" }'}
              spellCheck={false}
            />
          </label>
        )}
        {server && server.endpoints.length > 0 && (
          <div className="flex w-full flex-wrap items-center gap-1 text-xs">
            <span className="text-muted">Your endpoints:</span>
            {server.endpoints.map((e) => (
              <button
                key={e}
                type="button"
                className="rounded border border-line px-2 py-0.5 font-mono hover:bg-surface-2"
                onClick={() => {
                  const [m, p] = splitEndpoint(e);
                  setMethod(m);
                  setPath(p);
                  setNote(null);
                  selectBlank(p);
                }}
              >
                {e}
              </button>
            ))}
          </div>
        )}
      </form>

      <div aria-live="polite" className="px-3 pb-3">
        {note && <p className="text-sm text-muted">{note}</p>}
        {answer && <Answer answer={answer} />}
      </div>
    </section>
  );
}

function Answer({ answer }: { answer: CSharpHttpExchange }) {
  const tone = answer.status >= 500 ? 'border-bad text-bad' : answer.status >= 400 ? 'border-accent text-ink' : 'border-ok text-ink';
  const location = answer.headers.Location ?? answer.headers.location;
  return (
    <div className="rounded-card border border-line">
      <p className={`flex flex-wrap items-baseline gap-2 border-l-4 px-3 py-1 ${tone}`}>
        <span className="font-mono font-bold">{answer.status} {answer.reason}</span>
        <span className="text-sm text-muted">{statusInWords(answer.status)}</span>
      </p>
      {(answer.contentType || location) && (
        <p className="px-3 text-xs text-muted">
          {answer.contentType && <>Content-Type: <code>{answer.contentType}</code></>}
          {location && <> · Location: <code>{location}</code></>}
        </p>
      )}
      <pre className="m-2 max-h-56 overflow-auto rounded bg-[#1b2128] p-2 font-mono text-sm whitespace-pre-wrap break-words text-[#e8edf2]">
        {answer.body === '' ? '(no body)' : pretty(answer.body)}
      </pre>
    </div>
  );
}

/** Plain words for the status codes the course meets. */
export function statusInWords(status: number): string {
  switch (status) {
    case 200: return 'It worked.';
    case 201: return 'It worked, and something new was saved.';
    case 204: return 'It worked. There was nothing to send back.';
    case 400: return "The server couldn't use what was sent.";
    case 404: return 'Nothing was found at that path.';
    case 405: return "That path exists, but not for this method.";
    case 409: return 'That clashes with something already saved.';
    case 415: return "The server didn't understand the body's format.";
    case 500: return 'Your endpoint crashed. The Output panel says where.';
    default: return status >= 500 ? 'Something went wrong on the server.' : status >= 400 ? 'The request had a problem.' : '';
  }
}

function pretty(body: string): string {
  try {
    return JSON.stringify(JSON.parse(body), null, 2);
  } catch {
    return body;
  }
}

/** "GET /pets/{id:int}" → ["GET", "/pets/{id}"] (the constraint is for the server, not the address). */
function splitEndpoint(endpoint: string): [string, string] {
  const [methods = 'GET', path = '/'] = endpoint.split(' ');
  return [methods.split('/')[0] ?? 'GET', path.replace(/\{(\w+)[^}]*\}/g, '{$1}')];
}

/** The first route blank in a path, like {id} in /pets/{id}: where it is, and its name. */
export function findBlank(path: string): { start: number; end: number; name: string } | null {
  const match = /\{([^}]*)\}/.exec(path);
  if (!match) return null;
  const name = (match[1] ?? '').split(':')[0]?.replace(/[*?]/g, '').trim() || 'value';
  return { start: match.index, end: match.index + match[0].length, name };
}

/** Whether the server's routes constrain this blank to a number ({id:int}, {page:long}…). */
export function blankIsNumber(server: CSharpServerInfo | null, name: string): boolean {
  // The name comes from what the learner typed, so it's escaped before going into a pattern.
  const escaped = name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const pattern = new RegExp(`\\{\\*?${escaped}:(int|long|decimal|double|float|min|max|range)\\b`, 'i');
  return server?.endpoints.some((e) => pattern.test(e)) ?? false;
}

function firstPath(server: CSharpServerInfo | null): string | null {
  return server?.endpoints[0] ? splitEndpoint(server.endpoints[0])[1] : null;
}
