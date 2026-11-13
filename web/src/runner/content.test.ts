/**
 * Guards authored content: for every published HTML/CSS/JS/React challenge in the API's seed files,
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
import { describe, expect, it } from 'vitest';
import { stateToScript } from '../lesson/blocks/generator';
import { BLOCK_CATEGORIES } from '../lesson/blocks/toolbox';
import { describeTypeError } from './typeCheck';
import { nodeTypeChecker } from './typeCheckNode';
import { runInJsdom } from './runInJsdom';
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
    blocks?: { toolbox: string[]; starter?: object | null; solution?: object | null };
  };
}

const cases = readdirSync(coursesDir)
  .filter((f) => f.endsWith('.json'))
  .flatMap((file) => {
    const course = JSON.parse(readFileSync(coursesDir + file, 'utf8')) as { lessons: LessonFile[] };
    return course.lessons
      .filter((l) => l.isPublished && l.challenge && ['Html', 'Css', 'Js', 'React', 'TypeScript', 'Angular'].includes(l.challenge.kind))
      .map((l) => ({ name: `${file} → ${l.slug}`, lesson: l }));
  });

describe('authored challenges', () => {
  it('finds published challenges to check', () => expect(cases.length).toBeGreaterThan(0));

  it.each(cases)('$name', async ({ lesson }) => {
    const ch = lesson.challenge!;
    const tests: TestSpec[] = ch.tests.map((t, i) => ({ id: i + 1, ...t }));
    const failing = (results: TestResult[]) => results.filter((r) => !r.passed).map((r) => tests[r.testId - 1]?.label ?? `test ${r.testId}`);

    // elementCount only reads min/max; a spec without either (e.g. `equals`) would always pass.
    const countsWithoutBounds = tests.filter((t) => t.kind === 'elementCount' && t.spec.min === undefined && t.spec.max === undefined);
    expect(countsWithoutBounds.map((t) => t.label), 'elementCount tests need min and/or max').toEqual([]);

    const { results: solution } = await runInJsdom(ch.solutionFiles, tests);
    expect(solution).toHaveLength(tests.length);
    expect(failing(solution), 'solution fails these tests').toEqual([]);

    const { results: starter } = await runInJsdom(ch.starterFiles, tests);
    expect(failing(starter).length, 'starter code already passes every test').toBeGreaterThan(0);
  });
});

// Blocks mode: the blocks write script.js, so the same tests must work on generated code. The solution
// workspace (content-only, never sent to learners) proves the lesson can be solved with its toolbox.
const blockCases = cases.filter(({ lesson }) => lesson.challenge?.blocks);

describe('blocks challenges', () => {
  it('finds blocks challenges to check', () => expect(blockCases.length).toBeGreaterThan(0));

  it.each(blockCases)('$name', async ({ lesson }) => {
    const ch = lesson.challenge!;
    const blocks = ch.blocks!;
    expect(blocks.toolbox.filter((k) => !(BLOCK_CATEGORIES as string[]).includes(k)), 'unknown toolbox categories').toEqual([]);
    expect(blocks.solution, 'blocks challenges need a solution workspace').toBeTruthy();

    const tests: TestSpec[] = ch.tests.map((t, i) => ({ id: i + 1, ...t }));
    const failing = (results: TestResult[]) => results.filter((r) => !r.passed).map((r) => tests[r.testId - 1]?.label ?? `test ${r.testId}`);
    const withScript = (state: object | null | undefined) => ({ ...ch.starterFiles, 'script.js': stateToScript(state) });

    const { results: solution } = await runInJsdom(withScript(blocks.solution), tests);
    expect(failing(solution), 'blocks solution fails these tests').toEqual([]);

    const { results: starter } = await runInJsdom(withScript(blocks.starter), tests);
    expect(failing(starter).length, 'blocks starter already passes every test').toBeGreaterThan(0);
  });
});

// TypeScript and Angular lessons: the worked example has to be clean code (no type errors, nothing
// logged as an error), since learners copy from it. Its TypeScript is inline: <script type="text/typescript">.
const tsCases = cases.filter(({ lesson }) => lesson.challenge?.kind === 'TypeScript' || lesson.challenge?.kind === 'Angular');

describe('TypeScript examples', () => {
  it('finds TypeScript lessons to check', () => expect(tsCases.length).toBeGreaterThan(0));

  it.each(tsCases)('$name', async ({ lesson }) => {
    const example = (lesson as LessonFile & { exampleCode?: string }).exampleCode ?? '';
    const inline = /<script type="text\/typescript">([\s\S]*?)<\/script>/.exec(example)?.[1];
    expect(inline, 'example needs an inline <script type="text/typescript">').toBeTruthy();

    const diagnostics = await nodeTypeChecker({ 'example.ts': inline! });
    expect(diagnostics.map((d) => describeTypeError(d, false)), 'example has type errors').toEqual([]);

    const { results } = await runInJsdom({ 'index.html': example }, [
      { id: 1, label: 'runs', kind: 'noErrors', spec: {}, failMessage: '' },
      // Angular examples render into <app-root>; an empty one means the app never started.
      { id: 2, label: 'renders', kind: 'elementExists', spec: { selector: example.includes('<app-root') ? 'app-root *' : 'body' }, failMessage: '' },
    ]);
    expect(results[0]?.passed, 'example logs an error when it runs').toBe(true);
    expect(results[1]?.passed, 'example rendered nothing into <app-root>').toBe(true);
  });
});
