/**
 * Test-only helper: runs learner files exactly the way the preview does (composed page + harness +
 * practice API) in a fresh jsdom window with scripts enabled, and resolves with the harness's
 * results. Used by the content checks and the runner tests; never bundled into the app.
 */
import { JSDOM, VirtualConsole } from 'jsdom';
import { ANGULAR_RUN_OPTIONS, needsAngular } from './angular';
import { buildRunDocument, composeFiles, REACT_RUN_OPTIONS, type Files } from './compose';
import { hasTypeScript, loadToolkit, needsReact, needsReactRuntime } from './react';
import { nodeTypeChecker } from './typeCheckNode';
import type { ConsoleLine, TestResult, TestSpec } from './types';

export interface JsdomRun {
  results: TestResult[];
  console: ConsoleLine[];
}

/** Runs one page through the harness and resolves with its results (or throws if it never finishes). */
export async function runInJsdom(files: Files, tests: TestSpec[]): Promise<JsdomRun> {
  const runId = 'content-check';
  const compiled = needsReact(files);
  const react = needsReactRuntime(files);
  const angular = needsAngular(files);
  const kit = compiled ? await loadToolkit({ react, angular }) : undefined;
  // TypeScript pages are type-checked first, like the app does (with the `typescript` package here).
  const typeDiagnostics = hasTypeScript(files) ? await nodeTypeChecker(files) : undefined;
  const options = { ...(react ? REACT_RUN_OPTIONS : angular ? ANGULAR_RUN_OPTIONS : {}), typeDiagnostics };
  const dom = new JSDOM(buildRunDocument(composeFiles(files, kit), runId, tests, options), {
    runScripts: 'dangerously',
    pretendToBeVisual: true,
    virtualConsole: new VirtualConsole(), // learner console output is captured by the harness, not printed
  });
  const lines: ConsoleLine[] = [];
  return new Promise((done, fail) => {
    const timer = setTimeout(() => {
      dom.window.close();
      fail(new Error('page never reported results'));
    }, 4000);
    dom.window.addEventListener('message', (e: MessageEvent) => {
      const msg = e.data as { type?: string; runId?: string; results?: TestResult[]; level?: ConsoleLine['level']; text?: string };
      if (msg?.runId !== runId) return;
      if (msg.type === 'cg:console') lines.push({ level: msg.level ?? 'log', text: String(msg.text) });
      if (msg.type !== 'cg:done') return;
      clearTimeout(timer);
      dom.window.close();
      done({ results: msg.results ?? [], console: lines });
    });
  });
}
