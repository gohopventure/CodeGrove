/**
 * Vite plugin: `virtual:cg-react-runtime` exports React's development builds as ONE string of
 * JavaScript that registers each package with the preview's module loader (src/runner/moduleLoader.ts):
 *
 *   __cgDefine("react", function (module, exports, require, process) { ...react.development.js... });
 *
 * Why this shape:
 * - The preview frame has no network (CSP connect-src/script-src allow nothing external), so React
 *   has to travel inside the composed document, like every other script.
 * - React 19 ships no UMD build, only CommonJS files that `require` each other. A tiny in-frame
 *   `require` is simpler and smaller than bundling, and also lets learners write real `import`s.
 * - Development builds on purpose: they print readable errors and teaching-friendly warnings
 *   ("Each child in a list should have a unique key"). Production builds print minified error codes.
 *
 * The files are read from node_modules by path because react-dom's package "exports" doesn't expose
 * its cjs/ folder to `import`. The React version is whatever the app itself installs.
 */
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, join } from 'node:path';
import type { Plugin } from 'vite';

const VIRTUAL_ID = 'virtual:cg-react-runtime';
const RESOLVED_ID = '\0' + VIRTUAL_ID;

/** Module name → file inside its package. Order doesn't matter; modules load on first require. */
const MODULES: Record<string, [pkg: string, file: string]> = {
  react: ['react', 'cjs/react.development.js'],
  'react/jsx-runtime': ['react', 'cjs/react-jsx-runtime.development.js'],
  'react/jsx-dev-runtime': ['react', 'cjs/react-jsx-dev-runtime.development.js'],
  scheduler: ['scheduler', 'cjs/scheduler.development.js'],
  'react-dom': ['react-dom', 'cjs/react-dom.development.js'],
  'react-dom/client': ['react-dom', 'cjs/react-dom-client.development.js'],
};

export function buildReactRuntime(root: string): string {
  const require = createRequire(join(root, 'package.json'));
  const pkgDir = (pkg: string) => dirname(require.resolve(`${pkg}/package.json`));
  const version = (JSON.parse(readFileSync(join(pkgDir('react'), 'package.json'), 'utf8')) as { version: string }).version;

  const defines = Object.entries(MODULES).map(([name, [pkg, file]]) => {
    const source = readFileSync(join(pkgDir(pkg), file), 'utf8')
      // The runtime is inlined into an HTML <script>, so "</script" must never appear in it.
      .replace(/<\/script/gi, '<\\/script');
    return `__cgDefine(${JSON.stringify(name)}, function (module, exports, require, process) {\n${source}\n});`;
  });

  // No ".js" in the sourceURL: the harness points learners at files ending in .js/.jsx, and React's
  // own stack frames shouldn't look like one of theirs.
  return `/* React ${version} (development builds) for the Codegrove preview */\n${defines.join('\n')}\n//# sourceURL=react-runtime`;
}

export function reactRuntime(): Plugin {
  let root = process.cwd();
  return {
    name: 'cg-react-runtime',
    configResolved(config) {
      root = config.root;
    },
    resolveId(id) {
      return id === VIRTUAL_ID ? RESOLVED_ID : null;
    },
    load(id) {
      if (id !== RESOLVED_ID) return null;
      return `export default ${JSON.stringify(buildReactRuntime(root))};`;
    },
  };
}
