/*
 * A tiny CommonJS-style module system for React lessons. Like testEngine.ts, the function below runs
 * INSIDE the sandboxed preview frame: it is serialized with Function.prototype.toString(), so it must
 * stay fully self-contained (no imports, nothing from outside its own body and parameter).
 *
 * Learner files are compiled by Sucrase in the app (JSX + `import`/`export` → `require`/`exports`),
 * then registered here next to React's own modules (see vite-plugins/reactRuntime.ts):
 *
 *   __cgDefine("App.jsx", function (module, exports, require) { ... });
 *   __cgDefineCss("App.css", "h1 { color: teal; }");
 *   __cgStart("main.jsx");           // what <script type="module" src="main.jsx"> becomes
 *
 * Paths work the way they do in a Vite project: `import App from "./App"` finds App.tsx, App.ts, App.jsx or App.js,
 * and `import "./App.css"` adds the style sheet to the page.
 */

type Factory = (module: { exports: unknown }, exports: unknown, require: (name: string) => unknown, process: unknown) => void;

export function installModuleLoader(win: Window): void {
  const w = win as Window & Record<string, unknown>;
  if (typeof w.__cgDefine === 'function') return; // one loader per page, however many entry scripts

  const factories: Record<string, Factory> = {};
  const styles: Record<string, string> = {};
  const cache: Record<string, { exports: unknown }> = {};
  const loading: Record<string, boolean> = {};
  const process = { env: { NODE_ENV: 'development' } };

  const resolve = (request: string, from: string): string => {
    if (!request.startsWith('.') && !request.startsWith('/')) {
      if (factories[request] || styles[request]) return request;
      throw new Error(
        `Can't find the package "${request}". In the preview you can import "react", "react-dom/client" and your own files (like "./Card").`,
      );
    }
    // Relative to the importing file. Lesson files live in one folder, but nested paths still work.
    const parts = (request.startsWith('/') ? [] : from.split('/').slice(0, -1)).concat(request.split('/'));
    const out: string[] = [];
    for (const part of parts) {
      if (part === '..') out.pop();
      else if (part !== '.' && part !== '') out.push(part);
    }
    const path = out.join('/');
    for (const candidate of [path, `${path}.tsx`, `${path}.ts`, `${path}.jsx`, `${path}.js`, `${path}/index.tsx`, `${path}/index.ts`, `${path}/index.jsx`, `${path}/index.js`]) {
      if (factories[candidate] || styles[candidate] !== undefined) return candidate;
    }
    throw new Error(`Can't find the file "${request}" (imported in ${from}). Check the file name and its spelling.`);
  };

  const load = (name: string): unknown => {
    const cached = cache[name];
    if (cached) return cached.exports;

    if (styles[name] !== undefined) {
      const style = win.document.createElement('style');
      style.setAttribute('data-file', name);
      style.textContent = styles[name] ?? '';
      win.document.head.appendChild(style);
      cache[name] = { exports: {} };
      return cache[name].exports;
    }

    const factory = factories[name];
    if (!factory) throw new Error(`Can't find "${name}".`);
    if (loading[name]) {
      throw new Error(`${name} imports itself in a circle (A imports B, and B imports A). Move the shared code into its own file.`);
    }
    const module = { exports: {} as unknown };
    cache[name] = module;
    loading[name] = true;
    try {
      factory(module, module.exports, (request: string) => load(resolve(request, name)), process);
    } catch (error) {
      delete cache[name]; // a failed module shouldn't look loaded
      throw error;
    } finally {
      loading[name] = false;
    }
    return module.exports;
  };

  w.__cgDefine = (name: string, factory: Factory) => {
    factories[name] = factory;
  };
  w.__cgDefineCss = (name: string, css: string) => {
    styles[name] = css;
  };
  w.__cgRequire = (name: string) => load(resolve(name, ''));

  /** Runs an entry file once the HTML has been parsed, like a real <script type="module">. */
  w.__cgStart = (entry: string) => {
    const run = () => {
      try {
        load(resolve(entry.startsWith('.') || entry.startsWith('/') ? entry : `./${entry}`, ''));
      } catch (error) {
        const e = error as Error;
        // Compile errors already start with "App.jsx, line 3:", so they don't need "SyntaxError:" too.
        const located = /^[\w-]+\.(?:jsx?|tsx?)[,:]/.test(String(e?.message));
        const plain = !e || e.name === 'Error' || located;
        // An error thrown while a file first runs (top-level code, not a click or a render) is caught
        // here, so the page's error event never sees it. Name the learner's file and line from the
        // stack ourselves: each file is its own script with a //# sourceURL, so lines are the learner's.
        // Only frames in files this loader defined count (the browser's or jsdom's own files don't).
        let where = '';
        if (!located) {
          const frames = /([\w./-]+\.(?:jsx?|tsx?)):(\d+):\d+/g;
          for (let m = frames.exec(String(e?.stack ?? '')); m; m = frames.exec(String(e?.stack ?? ''))) {
            const file = m[1]!.split('/').pop()!;
            if (factories[file]) {
              where = ` (${file}, line ${m[2]})`;
              break;
            }
          }
        }
        console.error(e && e.message ? `${plain ? '' : `${e.name}: `}${e.message}${where}` : String(error));
      }
    };
    if (win.document.readyState === 'loading') win.document.addEventListener('DOMContentLoaded', run);
    else run();
  };
}
