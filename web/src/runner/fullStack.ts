/**
 * FullStack challenges (the capstone): an ASP.NET Core API in api/ and a React page in web/ that calls
 * it with fetch('/api/…'). The API runs in the C# runner as a live server (liveApi.ts); the page runs in
 * the preview with the fetch bridge on (mockApi.ts). Shared by the lesson page and the content checks.
 *
 * Which side checks a test (ContentSeeder enforces the same rules):
 * - C# kinds (httpRequest, csharpExpression, compileError, sourceMatches on an api/ file, …) → the API
 * - noErrors and consoleIncludes, which both sides have → whichever their spec's `side` says
 * - everything else (elementText, interact, jsExpression, …) → the page, with the API freshly started
 */
import type { TestResult, TestSpec } from './types';

export type Files = Record<string, string>;

const API_KINDS = new Set(['consoleEquals', 'compileError', 'compileWarning', 'noWarnings', 'csharpExpression', 'httpRequest', 'sourceMatches']);

export function testSide(test: TestSpec): 'api' | 'web' {
  if (test.kind === 'noErrors' || test.kind === 'consoleIncludes') return test.spec.side === 'api' ? 'api' : 'web';
  return API_KINDS.has(test.kind) ? 'api' : 'web';
}

export function splitTests(tests: TestSpec[]): { api: TestSpec[]; web: TestSpec[] } {
  return { api: tests.filter((t) => testSide(t) === 'api'), web: tests.filter((t) => testSide(t) === 'web') };
}

/** The API's files, names unchanged ("api/Program.cs"), so error messages and editor markers match the tabs. */
export function apiFiles(files: Files): Files {
  return Object.fromEntries(Object.entries(files).filter(([name]) => name.startsWith('api/')));
}

/** The page's files without the web/ prefix ("App.jsx", "index.html"), as the preview expects them. */
export function webFiles(files: Files): Files {
  return Object.fromEntries(Object.entries(files).filter(([name]) => name.startsWith('web/')).map(([name, code]) => [name.slice(4), code]));
}

/** Results back in the challenge's test order. Null if either side couldn't check at all. */
export function mergeResults(tests: TestSpec[], api: TestResult[] | null, web: TestResult[] | null): TestResult[] | null {
  if (!api || !web) return null;
  const byId = new Map([...api, ...web].map((r) => [r.testId, r]));
  const merged = tests.map((t) => byId.get(t.id));
  return merged.every((r): r is TestResult => r !== undefined) ? merged : null;
}

/** A FullStack lesson's worked example is a JSON object of files (exampleCode); null if it isn't one. */
export function parseExampleFiles(exampleCode: string | null | undefined): Files | null {
  if (!exampleCode) return null;
  try {
    const parsed = JSON.parse(exampleCode) as unknown;
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return null;
    return Object.values(parsed).every((v) => typeof v === 'string') ? (parsed as Files) : null;
  } catch {
    return null;
  }
}
