/**
 * Guards authored SQL content, like content.test.ts does for web challenges and
 * tests/Codegrove.CSharpRunner.Tests/ContentTests.cs for C#. For every published SQL challenge:
 * - the hidden .setup.sql builds;
 * - the solution passes all of its own tests, and the starter code fails at least one;
 * - the example runs against the challenge's database with no error and returns rows;
 * - every test kind is one the engine knows;
 * - sqlResultEquals tests that check order say so only when the solution sorts (ORDER BY), since
 *   SQL makes no promise about row order otherwise (the same goes for sortedBy).
 */
import { readdirSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import type { SqlJsStatic } from 'sql.js';
import { beforeAll, describe, expect, it } from 'vitest';
import { runSql, SETUP_FILE, SQL_TEST_KINDS, stripComments } from './sqlEngine';
import { loadSqlJs } from './sqlNode';
import type { TestResult, TestSpec } from './types';

const coursesDir = resolve(process.cwd(), '../src/Codegrove.Api/Content/courses') + '/';

interface LessonFile {
  slug: string;
  isPublished: boolean;
  exampleCode?: string;
  challenge?: {
    kind: string;
    starterFiles: Record<string, string>;
    solutionFiles: Record<string, string>;
    tests: { label: string; kind: TestSpec['kind']; spec: Record<string, unknown>; failMessage: string }[];
  };
}

const cases = readdirSync(coursesDir)
  .filter((f) => f.endsWith('.json'))
  .flatMap((file) => {
    const course = JSON.parse(readFileSync(coursesDir + file, 'utf8')) as { lessons: LessonFile[] };
    return course.lessons
      .filter((l) => l.isPublished && l.challenge?.kind === 'Sql')
      .map((l) => ({ name: `${file} → ${l.slug}`, lesson: l }));
  });

let SQL: SqlJsStatic;
beforeAll(async () => {
  SQL = await loadSqlJs();
}, 60_000);

describe('authored SQL challenges', () => {
  it('finds published SQL challenges to check', () => expect(cases.length).toBeGreaterThan(0));

  it.each(cases)('$name', ({ lesson }) => {
    const ch = lesson.challenge!;
    const setup = ch.starterFiles[SETUP_FILE] ?? '';
    const tests: TestSpec[] = ch.tests.map((t, i) => ({ id: i + 1, ...t }));
    const failing = (results: TestResult[]) =>
      results.filter((r) => !r.passed).map((r) => `${tests[r.testId - 1]?.label}: ${r.message}`);

    expect(tests.map((t) => t.kind).filter((k) => !(SQL_TEST_KINDS as readonly string[]).includes(k)), 'unknown test kinds').toEqual([]);

    const solution = runSql(SQL, { setup, files: ch.solutionFiles, tests });
    expect(solution.setupOk, `${SETUP_FILE} doesn't build: ${solution.console[0]?.text}`).toBe(true);
    expect(failing(solution.results), 'solution fails these tests').toEqual([]);

    const starter = runSql(SQL, { setup, files: ch.starterFiles, tests });
    expect(failing(starter.results).length, 'starter code already passes every test').toBeGreaterThan(0);

    if (lesson.exampleCode) {
      const example = runSql(SQL, { setup, files: { 'example.sql': lesson.exampleCode } });
      expect(example.error, 'the example has an error').toBeNull();
      expect(example.resultSets.length, 'the example returns no rows').toBeGreaterThan(0);
    }

    const solutionSql = Object.entries(ch.solutionFiles).filter(([n]) => !n.startsWith('.')).map(([, t]) => stripComments(t)).join('\n');
    const orderedWithoutSort = tests.filter((t) => t.kind === 'sqlResultEquals' && (t.spec.ordered || t.spec.sortedBy) && !/\border\s+by\b/i.test(solutionSql));
    expect(orderedWithoutSort.map((t) => t.label), 'ordered checks need an ORDER BY in the solution').toEqual([]);
  });
});
