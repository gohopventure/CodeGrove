/**
 * Vite plugin for Angular lessons. Two virtual modules, both loaded lazily by the app:
 *
 *   virtual:cg-angular-runtime  Angular's packages as ONE string of JavaScript that registers each
 *                               package with the preview's module loader (src/runner/moduleLoader.ts),
 *                               exactly like virtual:cg-react-runtime does for React.
 *   virtual:cg-angular-types    Angular's type definitions (.d.ts files + minimal package.json files),
 *                               keyed by path under /node_modules/, for both type checkers.
 *
 * How Angular runs without the Angular CLI:
 * - Angular ships ES modules only (fesm2022), and its packages import each other. Each package entry
 *   is bundled with Rolldown (Vite's own bundler) into one CommonJS module, keeping imports of OTHER
 *   packages external, so they resolve through the loader and every package exists exactly once
 *   (two copies of @angular/core would break dependency injection and signals).
 * - Templates are compiled in the browser by @angular/compiler (Angular's "JIT" mode). Real projects
 *   compile ahead of time with the CLI; the result on screen is the same, but template mistakes show
 *   up when the page runs rather than when it builds. That tradeoff keeps the sandbox server-free.
 * - No zone.js: Angular 21+ is zoneless by default, so signals drive change detection.
 * - Development mode on purpose: readable error messages (NG0… codes come with a sentence).
 *
 * The runtime is minified (whitespace and local names only; Angular's own error messages are strings and
 * survive), which brings ~2.8 MB down to roughly 1 MB. The packages are whatever the app installs
 * (devDependencies in package.json), so Angular is upgraded with npm like everything else.
 */
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, join, relative, sep } from 'node:path';
import type { Plugin } from 'vite';

const RUNTIME_ID = 'virtual:cg-angular-runtime';
const TYPES_ID = 'virtual:cg-angular-types';

/** Module names learners can import in the preview. tslib is a helper Angular's own code imports. */
export const ANGULAR_MODULES = [
  '@angular/core',
  '@angular/core/primitives/signals',
  '@angular/core/primitives/di',
  '@angular/common',
  '@angular/common/http',
  '@angular/compiler',
  '@angular/platform-browser',
  '@angular/forms',
  'rxjs',
  'tslib',
] as const;

/** Tiny modules written by hand. rxjs 7 exports every operator from its root as well. */
const SHIMS: Record<string, string> = {
  'rxjs/operators': "module.exports = require('rxjs');",
};

/**
 * Runs once per page after the packages are registered:
 * - loads @angular/compiler first, which registers the JIT compiler (in a CLI project the build does
 *   this job, so learners' main.ts looks exactly like a real one: no `import '@angular/compiler'`);
 * - defines __cgDecorate, which runs class decorators (src/runner/angular.ts lowers `@Component(...)`
 *   to a call of it, because browsers can't run decorators yet) and records signal inputs, models
 *   and outputs as Angular metadata;
 * - wraps bootstrapApplication so templateUrl/styleUrl files load from the lesson's own files first, and
 *   so the test harness can wait until the app has rendered and is stable
 *   (window.__cgBusy), after loading and after every click or keystroke a test makes.
 */
const GLUE = `(function () {
  var w = window;
  __cgRequire('@angular/compiler');
  var core = __cgRequire('@angular/core');
  // members: [[field, 'input' | 'model' | 'output', required]], found by src/runner/angular.ts. The
  // metadata is what the Angular CLI's JIT transform emits ("propDecorators", read by Angular's
  // reflection), so signal inputs, models and outputs work in templates.
  w.__cgDecorate = function (target, decorators, members) {
    if (members && members.length) {
      var props = Object.prototype.hasOwnProperty.call(target, 'propDecorators') ? target.propDecorators : {};
      members.forEach(function (m) {
        var name = m[0], kind = m[1], required = m[2];
        var list = props[name] || (props[name] = []);
        if (kind === 'input' || kind === 'model') list.push({ type: core.Input, args: [{ isSignal: true, alias: name, required: required, transform: undefined }] });
        if (kind === 'model') list.push({ type: core.Output, args: [name + 'Change'] });
        if (kind === 'output') list.push({ type: core.Output, args: [name] });
      });
      target.propDecorators = props;
    }
    for (var i = decorators.length - 1; i >= 0; i--) {
      var result = decorators[i](target);
      if (result !== undefined && result !== target) throw new Error('A class decorator replaced its class. Codegrove supports Angular decorators like @Component and @Injectable.');
    }
  };
  var booting = 0, apps = [];
  w.__cgBusy = function () {
    if (booting > 0) return true;
    for (var i = 0; i < apps.length; i++) if (!apps[i].stable) return true;
    return false;
  };
  var browser = __cgRequire('@angular/platform-browser');
  var original = browser.bootstrapApplication;
  // templateUrl / styleUrl(s), as in an Angular CLI project (app.ts + app.html + app.css): the files
  // come from the lesson itself (window.__cgSources, set by the composer), never from the network.
  var readLessonFile = function (url) {
    var name = String(url);
    while (name.indexOf('./') === 0) name = name.slice(2);
    while (name.charAt(0) === '/') name = name.slice(1);
    var sources = w.__cgSources || {};
    if (Object.prototype.hasOwnProperty.call(sources, name)) return Promise.resolve(sources[name]);
    return Promise.reject(new Error("Can't find the file " + JSON.stringify(String(url)) + ' (used as a templateUrl or styleUrl). Check the file name and its spelling.'));
  };
  var wrapped = function () {
    booting++;
    var self = this, args = arguments;
    var started = core.ɵresolveComponentResources(readLessonFile).then(function () {
      return original.apply(self, args);
    });
    started.then(function (appRef) {
      var app = { stable: false };
      apps.push(app);
      appRef.isStable.subscribe(function (stable) { app.stable = stable; });
      appRef.onDestroy(function () { app.stable = true; });
    }).then(function () { booting--; }, function () { booting--; });
    return started;
  };
  // Rolldown's CommonJS output defines exports as getters; redefine ours in place.
  Object.defineProperty(browser, 'bootstrapApplication', { value: wrapped, enumerable: true, configurable: true, writable: true });
})();`;

function define(name: string, source: string): string {
  // The runtime is inlined into an HTML <script>, so "</script" must never appear in it.
  const safe = source.replace(/<\/script/gi, '<\\/script');
  return `__cgDefine(${JSON.stringify(name)}, function (module, exports, require, process) {\n${safe}\n});`;
}

export async function buildAngularRuntime(root: string): Promise<string> {
  const { build } = await import('rolldown');
  const require = createRequire(join(root, 'package.json'));
  const version = (require('@angular/core/package.json') as { version: string }).version;

  const parts: string[] = [];
  for (const name of ANGULAR_MODULES) {
    const result = await build({
      cwd: root,
      input: name,
      write: false,
      // Everything outside this package entry stays an import, resolved by the in-frame loader.
      external: (id) => !id.startsWith('.') && !id.startsWith('/') && !id.startsWith('\0') && id !== name && !/^[A-Za-z]:[\\/]/.test(id),
      platform: 'browser',
      logLevel: 'silent',
      output: { format: 'cjs', minify: true, codeSplitting: false },
    });
    const chunk = result.output.find((o) => o.type === 'chunk');
    if (!chunk || !('code' in chunk)) throw new Error(`cg-angular-runtime: no output for ${name}`);
    parts.push(define(name, chunk.code));
  }
  for (const [name, code] of Object.entries(SHIMS)) parts.push(define(name, code));

  // No ".js"/".ts" in the sourceURL: the harness points learners at frames in their own files only.
  return `/* Angular ${version} for the Codegrove preview */\n${parts.join('\n')}\n${GLUE}\n//# sourceURL=angular-runtime`;
}

/**
 * Every .d.ts file a program importing all ANGULAR_MODULES reads (found by asking TypeScript itself),
 * plus a minimal package.json per package so "Bundler" module resolution finds them. Keys are
 * "/node_modules/<package>/<file>", the same layout in both checkers.
 */
export async function buildAngularTypes(root: string): Promise<Record<string, string>> {
  const { default: ts } = await import('typescript');
  const require = createRequire(join(root, 'package.json'));
  const nodeModules = join(dirname(require.resolve('@angular/core/package.json')), '..', '..');

  const entry = join(root, '__cg_angular_types_entry__.ts');
  const entrySource = [...ANGULAR_MODULES, 'rxjs/operators']
    .filter((m) => m !== 'tslib' && m !== '@angular/compiler')
    .map((m, i) => `import * as m${i} from '${m}'; void m${i};`)
    .join('\n');
  const options: import('typescript').CompilerOptions = {
    target: ts.ScriptTarget.ES2022,
    module: ts.ModuleKind.ESNext,
    moduleResolution: ts.ModuleResolutionKind.Bundler,
    noEmit: true,
    skipLibCheck: true,
    types: [],
  };
  const host = ts.createCompilerHost(options, true);
  const program = ts.createProgram([entry], options, {
    ...host,
    fileExists: (f) => f === entry || host.fileExists(f),
    readFile: (f) => (f === entry ? entrySource : host.readFile(f)),
    getSourceFile: (f, v, onError) => (f === entry ? ts.createSourceFile(f, entrySource, v, true) : host.getSourceFile(f, v, onError)),
  });

  const files: Record<string, string> = {};
  const packages = new Set<string>();
  for (const source of program.getSourceFiles()) {
    const rel = relative(nodeModules, source.fileName).split(sep).join('/');
    if (rel.startsWith('..') || source.fileName === entry || !rel.endsWith('.d.ts')) continue;
    if (rel.startsWith('typescript/')) continue; // the standard library; both checkers have it already
    files[`/node_modules/${rel}`] = source.text;
    packages.add(rel.startsWith('@') ? rel.split('/').slice(0, 2).join('/') : rel.split('/')[0]!);
  }

  // Minimal package.json files: name, version and the "types" half of each "exports" entry.
  for (const pkg of packages) {
    const full = JSON.parse(readFileSync(join(nodeModules, pkg, 'package.json'), 'utf8')) as {
      name: string;
      version: string;
      types?: string;
      typings?: string;
      exports?: Record<string, unknown>;
    };
    const exportsTypes: Record<string, { types: string }> = {};
    for (const [key, value] of Object.entries(full.exports ?? {})) {
      const types = typesOf(value);
      if (types) exportsTypes[key] = { types };
    }
    files[`/node_modules/${pkg}/package.json`] = JSON.stringify({
      name: full.name,
      version: full.version,
      types: full.types ?? full.typings,
      exports: exportsTypes,
    });
  }
  return files;
}

/** The "types" path inside one package.json "exports" value, however deeply conditions nest. */
function typesOf(value: unknown): string | undefined {
  if (!value || typeof value !== 'object') return undefined;
  const v = value as Record<string, unknown>;
  if (typeof v.types === 'string') return v.types;
  for (const key of ['import', 'default', 'node', 'browser']) {
    const found = typesOf(v[key]);
    if (found) return found;
  }
  return undefined;
}

/** The usual cause is packages missing from node_modules: say how to fix it instead of a require stack. */
function friendlyBuildError(error: unknown): string {
  const message = error instanceof Error ? error.message : String(error);
  if (/Cannot find (module|package) ['"]?(@angular|rxjs|tslib|rolldown)/.test(message)) {
    return `Angular lessons need packages that aren't installed yet. Stop the dev server, run "npm install" in web/, then start it again. (${message.split('\n')[0]})`;
  }
  return `Couldn't build the Angular preview runtime: ${message}`;
}

export function angularRuntime(): Plugin {
  let root = process.cwd();
  let runtime: Promise<string> | null = null;
  let types: Promise<Record<string, string>> | null = null;
  return {
    name: 'cg-angular-runtime',
    configResolved(config) {
      root = config.root;
    },
    resolveId(id) {
      return id === RUNTIME_ID || id === TYPES_ID ? '\0' + id : null;
    },
    async load(id) {
      // Built once per dev-server start (a few seconds) and reused by every page and test file.
      // A failed build isn't cached, so the next request retries (after `npm install`, say).
      try {
        if (id === '\0' + RUNTIME_ID) return `export default ${JSON.stringify(await (runtime ??= buildAngularRuntime(root)))};`;
        if (id === '\0' + TYPES_ID) return `export default ${JSON.stringify(await (types ??= buildAngularTypes(root)))};`;
      } catch (error) {
        if (id === '\0' + RUNTIME_ID) runtime = null;
        else types = null;
        throw new Error(friendlyBuildError(error));
      }
      return null;
    },
  };
}
