import { protectLoops } from './loopProtect';
import { installMockApi } from './mockApi';
import { composeReact, insertBeforeFirstScript, needsReact, type ReactToolkit } from './react';
import { evaluateTest, harnessMain } from './testEngine';
import { describeTypeError, type TypeDiagnostic } from './typeCheck';
import type { TestSpec } from './types';

export type Files = Record<string, string>;

/** Same policy as runner.html. Repeated in the composed page so it's present from the first byte. */
export const PREVIEW_CSP =
  "default-src 'none'; script-src 'unsafe-inline' 'unsafe-eval'; style-src 'unsafe-inline'; " +
  "img-src data: blob: https:; font-src data:; media-src data: blob:; connect-src 'none'; form-action 'none'; base-uri 'none'";

/**
 * Turns the learner's files into one HTML document: linked CSS and JS files are inlined (the frame
 * has no network), and every script gets loop protection. Only files the page actually links are
 * included, so learners still have to write the <link> and <script> tags themselves.
 */
export function composeFiles(files: Files, react?: ReactToolkit): string {
  // React pages (JSX files or inline JSX) are compiled first; see react.ts. Scripts that step creates
  // are marked data-cg-runtime and left alone below.
  const reactPage = react && needsReact(files) ? composeReact(files, react) : null;
  let html = reactPage?.html ?? files['index.html'] ?? '';

  html = html.replace(/<link\b[^>]*\bhref=["']([^"']+)["'][^>]*>/gi, (tag, href: string) => {
    const css = files[href.replace(/^\.\//, '')];
    return css !== undefined && /rel=["']?stylesheet/i.test(tag) ? `<style>\n${css}\n</style>` : tag;
  });

  html = html.replace(
    /<script\b(?![^>]*\bdata-cg-runtime)([^>]*)\bsrc=["']([^"']+)["']([^>]*)>\s*<\/script>/gi,
    (tag, _before: string, src: string) => {
      const js = files[src.replace(/^\.\//, '')];
      return js !== undefined ? `<script>\n${protectLoops(escapeScriptEnd(js))}\n</script>` : tag;
    },
  );

  // Inline <script> blocks without a src attribute.
  html = html.replace(/<script\b(?![^>]*\b(?:src=|data-cg-runtime))([^>]*)>([\s\S]*?)<\/script>/gi, (_tag, attrs: string, code: string) =>
    `<script${attrs}>${protectLoops(code)}</script>`,
  );

  return reactPage ? insertBeforeFirstScript(html, reactPage.runtime) : html;
}

export interface RunOptions {
  /** How long after "load" the tests start. */
  settleMs?: number;
  /** Pause between tests, so a framework can re-render after a test clicks or types. */
  stepDelayMs?: number;
  /** TypeScript pages: what the type checker found (an empty list means "checked, no errors"). */
  typeDiagnostics?: TypeDiagnostic[];
}

/** Timing for React pages: React renders asynchronously and re-renders after every click or keystroke. */
export const REACT_RUN_OPTIONS: RunOptions = { settleMs: 120, stepDelayMs: 30 };

/**
 * Builds the final document the runner writes: CSP meta + test harness + practice API (mockApi.ts)
 * first, then the learner's page.
 * The harness is serialized from real functions so the same code is unit-tested and shipped.
 */
export function buildRunDocument(learnerHtml: string, runId: string, tests: TestSpec[], options: RunOptions = {}): string {
  const { settleMs = 60, stepDelayMs = 0, typeDiagnostics } = options;
  const typeErrors = typeDiagnostics?.map((diagnostic) => ({ text: describeTypeError(diagnostic), diagnostic }));
  const config = JSON.stringify({ runId, tests, settleMs, stepDelayMs, typeErrors }).replace(/</g, '\\u003c');
  const harness =
    `<meta http-equiv="Content-Security-Policy" content="${PREVIEW_CSP}">` +
    `<script>(${harnessMain.toString()})(${config}, (${evaluateTest.toString()}));` +
    `(${installMockApi.toString()})(window);</script>`;
  return injectIntoHead(learnerHtml, harness);
}

/** Inserts markup at the start of <head>, creating one if the learner's page has none. */
export function injectIntoHead(html: string, snippet: string): string {
  const head = /<head\b[^>]*>/i.exec(html);
  if (head) return html.slice(0, head.index + head[0].length) + snippet + html.slice(head.index + head[0].length);

  const root = /<html\b[^>]*>/i.exec(html);
  if (root) return html.slice(0, root.index + root[0].length) + `<head>${snippet}</head>` + html.slice(root.index + root[0].length);

  const doctype = /^\s*<!doctype[^>]*>/i.exec(html);
  if (doctype) return doctype[0] + `<head>${snippet}</head>` + html.slice(doctype[0].length);

  return `<!DOCTYPE html><head>${snippet}</head>${html}`;
}

/** A "</script>" inside a JS string would end the inlined script early. */
function escapeScriptEnd(js: string): string {
  return js.replace(/<\/script/gi, '<\\/script');
}
