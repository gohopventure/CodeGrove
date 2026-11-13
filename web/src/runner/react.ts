/**
 * React support for the preview. Lessons are laid out like a small Vite project:
 *
 *   index.html   <div id="root"></div> <script type="module" src="main.jsx"></script>
 *   main.jsx     createRoot(document.getElementById("root")).render(<App />)
 *   App.jsx      export default function App() { ... }
 *   App.css      (optional, imported from a .jsx file)
 *
 * In the app, each .jsx/.js file is compiled with Sucrase (JSX and import/export only; it keeps line
 * numbers, so errors point at the learner's real lines), then the page gets: the module loader, React's
 * development builds, one script per learner file, and a start call where the module script was.
 * An example page can also use an inline <script type="text/jsx"> (or type="module") holding JSX.
 *
 * Sucrase and React (~1.3 MB together) are only downloaded for lessons that need them.
 *
 * TypeScript lessons use the same path: .ts/.tsx files (and inline <script type="text/typescript">)
 * have their types stripped by Sucrase, exactly like Vite does, so code with type errors still runs.
 * Type errors are found separately (typeCheck.ts) and reported by the typeCheck/typeError tests.
 */
import { DecoratorError, lowerDecorators } from './angular';
import { installModuleLoader } from './moduleLoader';
import { protectLoops } from './loopProtect';

export type Files = Record<string, string>;

/** What a React page needs from outside the frame. Loaded once, on first use. */
export interface ReactToolkit {
  /** Compiles one file's JSX and imports. Throws a SyntaxError with `loc` on bad code. */
  compile(code: string, fileName: string): string;
  /** React's development builds, registered with the module loader. */
  runtime: string;
}

const CODE_FILE = /\.(jsx|js|tsx|ts)$/i;
const TS_FILE = /\.tsx?$/i;
const INLINE_TYPES = ['text/jsx', 'text/babel', 'module', 'text/typescript', 'text/tsx'];
const INLINE_MODULE = /<script\b[^>]*\btype=["']?(?:text\/jsx|text\/babel|module|text\/typescript|text\/tsx)["']?[^>]*>/i;
const INLINE_REACT = /<script\b[^>]*\btype=["']?(?:text\/jsx|text\/babel|text\/tsx)["']?[^>]*>/i;
const IMPORTS_REACT = /\bfrom\s*["']react(?:-dom)?(?:\/[\w-]+)?["']|\brequire\(\s*["']react/;

/**
 * True when the page has to go through composeReact: JSX or TypeScript files, or an inline JSX,
 * TypeScript or module script. (The name predates TypeScript lessons; plain TS pages use the same
 * compile-and-load path, just without React.)
 */
export function needsReact(files: Files): boolean {
  if (Object.keys(files).some((f) => !f.startsWith('.') && /\.(jsx|tsx?)$/i.test(f) && !/\.d\.ts$/i.test(f))) return true;
  return INLINE_MODULE.test(files['index.html'] ?? '');
}

/**
 * True when the page actually uses React: a .jsx/.tsx file, an inline JSX script, or an import of
 * "react". TypeScript-only pages skip React's ~1.3 MB runtime.
 */
export function needsReactRuntime(files: Files): boolean {
  for (const [name, source] of Object.entries(files)) {
    if (name.startsWith('.')) continue;
    if (/\.(jsx|tsx)$/i.test(name)) return true;
    if (CODE_FILE.test(name) && IMPORTS_REACT.test(source)) return true;
  }
  const html = files['index.html'] ?? '';
  return INLINE_REACT.test(html) || IMPORTS_REACT.test(html);
}

/** True when any learner file is TypeScript (so the page gets type-checked as well as run). */
export function hasTypeScript(files: Files): boolean {
  return Object.keys(files).some((f) => !f.startsWith('.') && TS_FILE.test(f));
}

type Sucrase = typeof import('sucrase');
let compiler: Promise<Sucrase> | null = null;
/** Framework runtimes (big strings of JavaScript for the frame), each downloaded on first use. */
const runtimes: Record<'react' | 'angular', Promise<string> | null> = { react: null, angular: null };

/** Sucrase transforms by file type. Types are stripped, never checked: checking is typeCheck.ts's job. */
function transformsFor(fileName: string): ('jsx' | 'typescript' | 'imports')[] {
  if (/\.tsx$/i.test(fileName)) return ['typescript', 'jsx', 'imports'];
  if (/\.ts$/i.test(fileName)) return ['typescript', 'imports'];
  return ['jsx', 'imports'];
}

/** Downloads a runtime chunk once; a failed download is forgotten, so the next run can retry it. */
function once(key: keyof typeof runtimes, load: () => Promise<{ default: string }>): Promise<string> {
  runtimes[key] ??= load()
    .then((m) => m.default)
    .catch((error: unknown) => {
      runtimes[key] = null;
      throw error;
    });
  return runtimes[key];
}

/**
 * Loads what a compiled page needs, once each: Sucrase always; React's or Angular's runtime only when
 * asked. A failed download is forgotten, so the next run can retry it.
 *
 * TypeScript files go through lowerDecorators (angular.ts) before Sucrase, so Angular's
 * `@Component(...)` classes become plain JavaScript; files without decorators are untouched.
 */
export function loadToolkit({ react, angular = false }: { react: boolean; angular?: boolean }): Promise<ReactToolkit> {
  compiler ??= import('sucrase').catch((error: unknown) => {
    compiler = null;
    throw error;
  });
  return Promise.all([
    compiler,
    react ? once('react', () => import('virtual:cg-react-runtime')) : Promise.resolve(''),
    angular ? once('angular', () => import('virtual:cg-angular-runtime')) : Promise.resolve(''),
  ]).then(([sucrase, reactCode, angularCode]) => ({
    runtime: reactCode + (reactCode && angularCode ? '\n' : '') + angularCode,
    compile: (code: string, fileName: string) =>
      sucrase.transform(TS_FILE.test(fileName) ? lowerDecorators(code, fileName) : code, {
        transforms: transformsFor(fileName),
        jsxRuntime: 'automatic', // like Vite and create-react-app: no `import React` needed for JSX
        production: false, // jsxDEV keeps file/line info for React's warnings
        filePath: fileName,
      }).code,
  }));
}

/** Sucrase plus React's runtime. Kept for existing callers. */
export function loadReactToolkit(): Promise<ReactToolkit> {
  return loadToolkit({ react: true });
}

export interface ReactPage {
  /** index.html with each module/JSX script replaced by a small data-cg-runtime start script. */
  html: string;
  /** Loader + React + the learner's compiled files. Insert it before the page's first script. */
  runtime: string;
}

/**
 * Prepares a React page for the generic composer (compose.ts). Module/JSX scripts become start calls
 * marked data-cg-runtime, which composeFiles leaves untouched; the runtime markup is added after the
 * generic pass, so nothing inside React's source is ever mistaken for a learner's tag. Learner files
 * get loop protection here, after compiling (React's own loops must not get a time limit).
 * Returns null when the page has no module or JSX script after all.
 */
export function composeReact(files: Files, kit: ReactToolkit): ReactPage | null {
  let html = files['index.html'] ?? '';
  const modules: string[] = [];
  const starts: string[] = [];

  // Every learner .jsx/.js file and .css file becomes a module, whether or not anything imports it
  // (unused ones simply never run). index.html and dotfiles aren't modules.
  for (const [name, source] of Object.entries(files)) {
    if (name.startsWith('.')) continue;
    if (CODE_FILE.test(name)) modules.push(defineModule(name, source, kit));
    else if (/\.css$/i.test(name)) modules.push(`<script data-cg-runtime>__cgDefineCss(${scriptString(name)}, ${scriptString(source)});</script>`);
  }

  let inlineCount = 0;
  html = html.replace(/<script\b([^>]*)>([\s\S]*?)<\/script>/gi, (tag, attrs: string, body: string) => {
    const type = /\btype=["']?([^"'\s>]+)/i.exec(attrs)?.[1]?.toLowerCase();
    const src = /\bsrc=["']([^"']+)["']/i.exec(attrs)?.[1];
    const isModule = !!type && INLINE_TYPES.includes(type);

    if (src && CODE_FILE.test(src) && files[src.replace(/^\.?\//, '')] !== undefined && (isModule || /\.(jsx|tsx?)$/i.test(src))) {
      starts.push(src.replace(/^\.?\//, ''));
      return `<script data-cg-runtime>__cgStart(${scriptString(src.replace(/^\.?\//, ''))});</script>`;
    }
    if (!src && isModule) {
      const ext = type === 'text/typescript' ? 'ts' : type === 'text/tsx' ? 'tsx' : 'jsx';
      const name = `inline-script-${++inlineCount}.${ext}`;
      modules.push(defineModule(name, body, kit));
      starts.push(name);
      return `<script data-cg-runtime>__cgStart(${scriptString(name)});</script>`;
    }
    return tag;
  });

  if (starts.length === 0) return null;

  const sources = Object.fromEntries(Object.entries(files).filter(([n]) => !n.startsWith('.') && n !== 'index.html'));
  const runtime =
    `<script data-cg-runtime>(${installModuleLoader.toString()})(window);` +
    // Original (uncompiled) learner files, for "did you use useState?"-style checks in lesson tests.
    `window.__cgSources = ${scriptJson(sources)};</script>` +
    `<script data-cg-runtime>${kit.runtime}</script>` +
    modules.join('');

  return { html, runtime };
}

/** One learner file as its own script, so the browser can name the file (and line) in errors. */
function defineModule(name: string, source: string, kit: ReactToolkit): string {
  let body: string;
  try {
    // A shorter limit than plain pages: React may run a failing render twice before reporting it,
    // and a component should never loop for anywhere near this long anyway.
    body = protectLoops(kit.compile(source, name), 700);
  } catch (error) {
    // The module throws when it's loaded, so the error shows up once, with the file and line.
    body = `throw new SyntaxError(${JSON.stringify(friendlySyntaxError(error, name))});`;
  }
  // The factory opens on the same line as the learner's first line, so line numbers stay true.
  return (
    `<script data-cg-runtime>__cgDefine(${scriptString(name)}, function (module, exports, require) {${escapeScriptEnd(body)}\n});` +
    `\n//# sourceURL=${encodeURI(name)}</script>`
  );
}

/** "Unexpected token (3:12)" → "App.jsx, line 3: Unexpected token. …" */
export function friendlySyntaxError(error: unknown, fileName: string): string {
  // Decorator problems (angular.ts) are already written in the learner's terms, file and line included.
  if (error instanceof DecoratorError) return error.message;
  const e = error as { message?: string; loc?: { line: number; column: number } };
  const raw = String(e?.message ?? error)
    .replace(/^Error transforming [^:]+:\s*/, '') // Sucrase names the file; we say it our way below
    .replace(/\s*\(\d+:\d+\)\s*$/, '');
  const line = e?.loc?.line;
  let tip = '';
  if (/unterminated jsx|expected closing|closing tag/i.test(raw)) tip = ' Check that every tag you open is closed, like <p>…</p> or <img />.';
  else if (/adjacent jsx|jsx elements must be wrapped/i.test(raw)) tip = ' A component can only return one outer element. Wrap them in a <div> or <>…</>.';
  else if (/unexpected token/i.test(raw)) tip = ' Look for a missing bracket, brace, quote or comma on or just before that line.';
  return `${fileName}${line ? `, line ${line}` : ''}: ${raw}.${tip}`;
}

export function insertBeforeFirstScript(html: string, markup: string): string {
  const first = /<script\b/i.exec(html);
  if (first) return html.slice(0, first.index) + markup + html.slice(first.index);
  const bodyEnd = /<\/body>/i.exec(html);
  if (bodyEnd) return html.slice(0, bodyEnd.index) + markup + html.slice(bodyEnd.index);
  return html + markup;
}

/** A JS string literal that is safe inside an HTML <script>. */
function scriptString(text: string): string {
  return JSON.stringify(text).replace(/</g, '\\u003c');
}

function scriptJson(value: unknown): string {
  return JSON.stringify(value).replace(/</g, '\\u003c');
}

function escapeScriptEnd(js: string): string {
  return js.replace(/<\/script/gi, '<\\/script');
}
