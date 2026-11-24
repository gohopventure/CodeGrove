/*
 * The practice API for the fetch lessons. The preview frame has no network at all (CSP
 * connect-src 'none'), so this replaces window.fetch inside the frame with a tiny in-memory server.
 * Learners call it with real-looking URLs, e.g. fetch('https://api.codegrove.test/planets'), and
 * get back something that behaves like a real Response: ok, status, json(), text().
 *
 * Like the harness, installMockApi runs INSIDE the frame: it's serialized with toString(), so it
 * must be fully self-contained (no imports, no outside references). It's also imported normally by
 * unit tests.
 *
 * Endpoints (GET only; anything else gets 405):
 *   /planets        all eight planets          /planets/:id   one planet, or 404
 *   /pets           shelter pets up for adoption
 *   /weather        today's weather in a made-up town
 *
 * The harness waits for in-flight requests (window.__cgMockPending) before it runs the tests, so a
 * page that fetches on load is checked after its data has arrived.
 *
 * Live API (capstone): when `live` is given, the learner's own server answers too. A relative URL
 * (fetch('/api/clubs')) or a localhost one (fetch('http://localhost:5080/api/clubs'), as in the
 * downloaded project) is posted to the app as { type: 'cg:fetch', runId, id, method, path, headers,
 * body }; the app sends it to the server running in the C# runner and answers with { type:
 * 'cg:fetch-reply', id, status, statusText, headers, body } or { …, error }. The frame still has no
 * network: the request never leaves the browser tab.
 */

/** The only host the practice API answers on. `.test` is reserved, so it can never be a real site. */
export const MOCK_API_ORIGIN = 'https://api.codegrove.test';

export interface LiveApiConfig {
  runId: string;
}

/** A request the preview frame asks the app to send to the learner's live server. */
export interface LiveFetchMessage {
  type: 'cg:fetch';
  runId: string;
  id: string;
  method: string;
  /** Path and query, e.g. "/api/clubs?kind=owl". */
  path: string;
  headers: Record<string, string>;
  body: string | null;
}

/** The app's answer to a LiveFetchMessage. */
export interface LiveFetchReply {
  type: 'cg:fetch-reply';
  id: string;
  status?: number;
  statusText?: string;
  headers?: Record<string, string>;
  body?: string;
  /** Set when there was no answer at all; the fetch rejects and this is logged to the console. */
  error?: string;
}

export function installMockApi(win: Window, delayMs = 40, live?: LiveApiConfig | null): void {
  const origin = 'https://api.codegrove.test';
  const planets = [
    { id: 1, name: 'Mercury', moons: 0, hasRings: false, type: 'rocky' },
    { id: 2, name: 'Venus', moons: 0, hasRings: false, type: 'rocky' },
    { id: 3, name: 'Earth', moons: 1, hasRings: false, type: 'rocky' },
    { id: 4, name: 'Mars', moons: 2, hasRings: false, type: 'rocky' },
    { id: 5, name: 'Jupiter', moons: 95, hasRings: true, type: 'gas giant' },
    { id: 6, name: 'Saturn', moons: 146, hasRings: true, type: 'gas giant' },
    { id: 7, name: 'Uranus', moons: 28, hasRings: true, type: 'ice giant' },
    { id: 8, name: 'Neptune', moons: 16, hasRings: true, type: 'ice giant' },
  ];
  const pets = [
    { id: 1, name: 'Biscuit', species: 'dog', age: 3, adopted: false },
    { id: 2, name: 'Pickles', species: 'cat', age: 1, adopted: true },
    { id: 3, name: 'Turbo', species: 'turtle', age: 12, adopted: false },
    { id: 4, name: 'Nugget', species: 'hamster', age: 1, adopted: false },
    { id: 5, name: 'Mango', species: 'parrot', age: 7, adopted: true },
  ];
  const weather = { town: 'Maple Hollow', tempC: 18, conditions: 'partly cloudy', windKph: 12 };

  const route = (method: string, path: string): { status: number; body: unknown } => {
    if (method !== 'GET') return { status: 405, body: { error: `The practice API only supports GET, not ${method}.` } };
    if (path === '/planets') return { status: 200, body: planets };
    const one = /^\/planets\/(\d+)$/.exec(path);
    if (one) {
      const planet = planets.find((p) => p.id === Number(one[1]));
      return planet ? { status: 200, body: planet } : { status: 404, body: { error: `No planet with id ${one[1]}.` } };
    }
    if (path === '/pets') return { status: 200, body: pets };
    if (path === '/weather') return { status: 200, body: weather };
    return { status: 404, body: { error: `Nothing at ${path}. Try /planets, /pets or /weather.` } };
  };

  const reasons: Record<number, string> = { 200: 'OK', 404: 'Not Found', 405: 'Method Not Allowed' };
  const w = win as Window & { __cgMockPending?: number };
  w.__cgMockPending = 0;

  // ---- Live API (capstone) ----
  const LIVE_TIMEOUT_MS = 20000;
  const waiting = new Map<string, (reply: LiveFetchReply) => void>();
  let nextId = 0;
  if (live) {
    (w as Window & { __cgLiveLog?: string[] }).__cgLiveLog = [];
    win.addEventListener('message', (event: MessageEvent) => {
      // Only the app (our parent) answers. (In tests the page is its own parent.)
      if (win.parent !== win && event.source !== win.parent) return;
      const reply = event.data as LiveFetchReply;
      if (reply?.type !== 'cg:fetch-reply' || typeof reply.id !== 'string') return;
      const done = waiting.get(reply.id);
      if (done) {
        waiting.delete(reply.id);
        done(reply);
      }
    });
  }
  const isLocalhost = (u: URL) => /^(localhost|127\.0\.0\.1|\[::1\])$/.test(u.hostname);
  const isRelative = (raw: string) => !/^[a-z][a-z\d+.-]*:/i.test(raw) && !raw.startsWith('//');

  const toHeaders = (h: unknown): Record<string, string> => {
    const out: Record<string, string> = {};
    if (!h) return out;
    const entries: [string, string][] =
      typeof (h as { forEach?: unknown }).forEach === 'function' && !Array.isArray(h)
        ? (() => {
            const list: [string, string][] = [];
            (h as { forEach: (cb: (v: string, k: string) => void) => void }).forEach((v, k) => list.push([k, v]));
            return list;
          })()
        : Array.isArray(h)
          ? (h as [string, string][])
          : Object.entries(h as Record<string, string>);
    for (const [k, v] of entries) out[String(k).toLowerCase()] = String(v);
    return out;
  };

  const makeResponse = (status: number, statusText: string, headers: Record<string, string>, body: string, href: string): unknown => {
    const R = (win as Window & { Response?: typeof Response }).Response;
    if (typeof R === 'function') {
      // A real Response, so headers.get(), clone() and friends work as they do against a real server.
      const noBody = status === 204 || status === 205 || status === 304;
      const response = new R(noBody ? null : body, { status, statusText, headers });
      Object.defineProperty(response, 'url', { value: href });
      return response;
    }
    let used = false;
    const read = () => {
      if (used) return Promise.reject(new TypeError('Body has already been read. Call json() only once.'));
      used = true;
      return Promise.resolve(body);
    };
    const lower: Record<string, string> = {};
    for (const k of Object.keys(headers)) lower[k.toLowerCase()] = headers[k] as string;
    return {
      ok: status >= 200 && status < 300, status, statusText, url: href,
      headers: { get: (name: string) => lower[name.toLowerCase()] ?? null },
      json: () => read().then((t) => JSON.parse(t)),
      text: read,
    };
  };

  const liveFetch = (url: URL, input: unknown, init?: { method?: string; headers?: unknown; body?: unknown }): Promise<unknown> => {
    const request = input as { method?: string; headers?: unknown } | null;
    const method = String(init?.method ?? (typeof input === 'object' ? request?.method : undefined) ?? 'GET').toUpperCase();
    const headers = toHeaders(init?.headers ?? (typeof input === 'object' ? request?.headers : undefined));
    const body = init?.body === undefined || init?.body === null ? null : String(init.body);
    if (body !== null && !headers['content-type']) headers['content-type'] = 'text/plain;charset=UTF-8';
    const id = `live-${++nextId}`;
    // Every request the page sent to its API, for checks: window.__cgLiveLog lists "GET /api/clubs"-style
    // entries, so a check can count them (an effect without its dependency array fetches again and again).
    const log = ((w as Window & { __cgLiveLog?: string[] }).__cgLiveLog ??= []);
    if (log.length < 1000) log.push(`${method} ${url.pathname + url.search}`);
    const message: LiveFetchMessage = { type: 'cg:fetch', runId: live!.runId, id, method, path: url.pathname + url.search, headers, body };

    w.__cgMockPending = (w.__cgMockPending ?? 0) + 1;
    return new Promise<unknown>((resolve, reject) => {
      const timer = setTimeout(() => {
        waiting.delete(id);
        finish({ type: 'cg:fetch-reply', id, error: `${method} ${message.path} got no answer from your server.` });
      }, LIVE_TIMEOUT_MS);
      const finish = (reply: LiveFetchReply) => {
        clearTimeout(timer);
        w.__cgMockPending = Math.max(0, (w.__cgMockPending ?? 1) - 1);
        if (reply.error !== undefined || typeof reply.status !== 'number') {
          console.error(reply.error ?? `${method} ${message.path} got no answer from your server.`);
          reject(new TypeError('Failed to fetch'));
          return;
        }
        resolve(makeResponse(reply.status, reply.statusText ?? '', reply.headers ?? {}, reply.body ?? '', url.href));
      };
      waiting.set(id, finish);
      win.parent.postMessage(message, '*');
    });
  };

  const mockFetch = (input: unknown, init?: { method?: string; headers?: unknown; body?: unknown }): Promise<unknown> => {
    const raw = String((input as { url?: string })?.url ?? input);
    let url: URL;
    try {
      url = new URL(raw, origin);
    } catch {
      return Promise.reject(new TypeError(`Failed to fetch: "${String(input)}" is not a valid URL.`));
    }
    if (live && (isRelative(raw) || isLocalhost(url))) {
      return liveFetch(isRelative(raw) ? new URL(raw, 'http://localhost:5000') : url, input, init);
    }
    if (url.origin !== origin) {
      // Same error a real browser gives when it can't reach a server, plus a pointer to what works.
      console.info(`The preview can only reach the practice API at ${origin}, not ${url.origin}.`);
      return Promise.reject(new TypeError('Failed to fetch'));
    }

    const method = String(init?.method ?? 'GET').toUpperCase();
    const { status, body } = route(method, url.pathname.replace(/\/+$/, '') || '/');
    const text = JSON.stringify(body);
    let used = false;
    const read = () => {
      if (used) return Promise.reject(new TypeError('Body has already been read. Call json() only once.'));
      used = true;
      return Promise.resolve(text);
    };
    const response = {
      ok: status >= 200 && status < 300,
      status,
      statusText: reasons[status] ?? '',
      url: url.href,
      headers: { get: (name: string) => (name.toLowerCase() === 'content-type' ? 'application/json' : null) },
      json: () => read().then((t) => JSON.parse(t)),
      text: read,
    };

    w.__cgMockPending = (w.__cgMockPending ?? 0) + 1;
    return new Promise((resolve) =>
      setTimeout(() => {
        w.__cgMockPending = Math.max(0, (w.__cgMockPending ?? 1) - 1);
        resolve(response);
      }, delayMs),
    );
  };

  Object.defineProperty(win, 'fetch', { value: mockFetch, writable: true, configurable: true });
}
