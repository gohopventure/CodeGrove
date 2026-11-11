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
 */

/** The only host the practice API answers on. `.test` is reserved, so it can never be a real site. */
export const MOCK_API_ORIGIN = 'https://api.codegrove.test';

export function installMockApi(win: Window, delayMs = 40): void {
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

  const mockFetch = (input: unknown, init?: { method?: string }): Promise<unknown> => {
    let url: URL;
    try {
      url = new URL(String((input as { url?: string })?.url ?? input), origin);
    } catch {
      return Promise.reject(new TypeError(`Failed to fetch: "${String(input)}" is not a valid URL.`));
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
