/**
 * Guards authored content: for every published HTML/CSS/JS challenge in the API's seed files,
 * the solution must pass all of its own tests and the starter code must fail at least one.
 * Catches typos in selectors, solutions that don't match their tests, and challenges that are
 * already "solved" before the learner touches them.
 *
 * Each case is run the way the preview runs it: the composed page plus the real harness and
 * practice API, in a fresh jsdom window with scripts enabled. So JavaScript challenges are checked
 * against what the learner's code actually does (console output, DOM changes, fetch results).
 */
import { readdirSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { JSDOM, VirtualConsole } from 'jsdom';
import { describe, expect, it } from 'vitest';
import { buildRunDocument, composeFiles } from './compose';
import type { TestResult, TestSpec } from './types';

// Vitest runs from web/ (the package root), so content is one level up.
const coursesDir = resolve(process.cwd(), '../src/Codegrove.Api/Content/courses') + '/';

interface LessonFile {
  slug: string;
  isPublished: boolean;
  challenge?: {
    kind: string;
    starterFiles: Record<string, string>;
    solutionFiles: Record<string, string>;
    tests: { label: string; kind: TestSpec['kind']; spec: Record<string, unknown>; failMessage: string }[];
  };
}

/** Runs one page through the harness and resolves with its results (or throws if it never finishes). */
export function runInJsdom(files: Record<string, string>, tests: TestSpec[]): Promise<TestResult[]> {
  const runId = 'content-check';
  const dom = new JSDOM(buildRunDocument(composeFiles(files), runId, tests), {
    runScripts: 'dangerously',
    pretendToBeVisual: true,
    virtualConsole: new VirtualConsole(), // learner console output is captured by the harness, not printed
  });
  return new Promise((done, fail) => {
    const timer = setTimeout(() => {
      dom.window.close();
      fail(new Error('page never reported results'));
    }, 4000);
    dom.window.addEventListener('message', (e: MessageEvent) => {
      const msg = e.data as { type?: string; runId?: string; results?: TestResult[] };
      if (msg?.type !== 'cg:done' || msg.runId !== runId) return;
      clearTimeout(timer);
      dom.window.close();
      done(msg.results ?? []);
    });
  });
}

const cases = readdirSync(coursesDir)
  .filter((f) => f.endsWith('.json'))
  .flatMap((file) => {
    const course = JSON.parse(readFileSync(coursesDir + file, 'utf8')) as { lessons: LessonFile[] };
    return course.lessons
      .filter((l) => l.isPublished && l.challenge && ['Html', 'Css', 'Js'].includes(l.challenge.kind))
      .map((l) => ({ name: `${file} → ${l.slug}`, lesson: l }));
  });

describe('authored challenges', () => {
  it('finds published challenges to check', () => expect(cases.length).toBeGreaterThan(0));

  it.each(cases)('$name', async ({ lesson }) => {
    const ch = lesson.challenge!;
    const tests: TestSpec[] = ch.tests.map((t, i) => ({ id: i + 1, ...t }));
    const failing = (results: TestResult[]) => results.filter((r) => !r.passed).map((r) => tests[r.testId - 1].label);

    const solution = await runInJsdom(ch.solutionFiles, tests);
    expect(solution).toHaveLength(tests.length);
    expect(failing(solution), 'solution fails these tests').toEqual([]);

    const starter = await runInJsdom(ch.starterFiles, tests);
    expect(failing(starter).length, 'starter code already passes every test').toBeGreaterThan(0);
  });
});
