/**
 * Guards authored FullStack (capstone) challenges end to end, like content.test.ts, sqlContent.test.ts and
 * ContentTests.cs do for the other kinds. The learner's API runs on the real C# runner in a helper process
 * (tests/Codegrove.FullStackHost, over stdin/stdout); the page runs in jsdom with the fetch bridge pointed
 * at that API, the same way the lesson page wires them together. For every published FullStack challenge:
 * - the solution passes every test, and the starter fails at least one;
 * - the worked example's API starts, and its page loads without errors.
 *
 * Needs the .NET SDK. Locally without it these tests are skipped; in CI (CI=true) they must run.
 */
import { spawn, spawnSync, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { readdirSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { createInterface } from 'node:readline';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import type { CSharpRunResult, LiveServerReply } from './csharp';
import { apiFiles, mergeResults, parseExampleFiles, splitTests, webFiles } from './fullStack';
import { runInJsdom, type LiveApiHandler } from './runInJsdom';
import type { TestResult, TestSpec } from './types';

const repo = resolve(process.cwd(), '..');
const coursesDir = `${repo}/src/Codegrove.Api/Content/courses/`;

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
      .filter((l) => l.isPublished && l.challenge?.kind === 'FullStack')
      .map((l) => ({ name: `${file} → ${l.slug}`, lesson: l }));
  });

const hasDotnet = spawnSync('dotnet', ['--version']).status === 0;
if (!hasDotnet && process.env.CI) throw new Error('FullStack content checks need the .NET SDK in CI.');

/** The helper process: one request line in, one answer line out, in order. */
class Host {
  private readonly proc: ChildProcessWithoutNullStreams;
  private readonly waiting: ((line: string) => void)[] = [];

  constructor() {
    this.proc = spawn('dotnet', ['run', '--project', `${repo}/tests/Codegrove.FullStackHost`, '--verbosity', 'quiet'], { cwd: repo });
    createInterface({ input: this.proc.stdout }).on('line', (line) => this.waiting.shift()?.(line));
    // The host's own errors (a crash would otherwise look like a hang).
    this.proc.stderr.on('data', (chunk: Buffer) => process.stderr.write(`[FullStackHost] ${chunk.toString()}`));
    this.proc.on('exit', (code) => {
      for (const done of this.waiting.splice(0)) done(JSON.stringify({ hostExited: code }));
    });
  }

  private ask<T>(message: object): Promise<T> {
    return new Promise((done) => {
      this.waiting.push((line) => done(JSON.parse(line) as T));
      this.proc.stdin.write(JSON.stringify(message) + '\n');
    });
  }

  start(files: Record<string, string>, tests: TestSpec[]): Promise<CSharpRunResult> {
    return this.ask({ op: 'start', run: { files: apiFiles(files), tests } });
  }

  send(session: string, request: { method: string; path: string; body?: string; headers?: Record<string, string> }): Promise<LiveServerReply> {
    return this.ask({ op: 'send', live: { session, request } });
  }

  stop() {
    this.proc.stdin.end();
    this.proc.kill();
  }
}

let host: Host;
beforeAll(async () => {
  if (!hasDotnet || cases.length === 0) return;
  host = new Host();
  // The first start pays for `dotnet run`'s build and Roslyn's warm-up.
  await host.start({ 'api/Program.cs': 'var app = WebApplication.CreateBuilder(args).Build();\napp.Run();' }, []);
}, 240_000);
afterAll(() => host?.stop());

/** Runs a FullStack app as the lesson page does: start the API with its tests, then the page with its tests. */
async function runApp(files: Record<string, string>, tests: TestSpec[]) {
  const sides = splitTests(tests);
  const started = await host.start(files, sides.api);
  const session = started.liveSession;
  const liveApi: LiveApiHandler = async (r) => {
    if (!session) return { error: 'The API did not start.' };
    const reply = await host.send(session, { method: r.method, path: r.path, body: r.body ?? undefined, headers: r.headers });
    if (!reply.found || !reply.exchange) return { error: reply.console.map((l) => l.text).join(' ') || 'no answer' };
    const { status, reason, headers, body } = reply.exchange;
    return { status, statusText: reason, headers, body };
  };
  const page = await runInJsdom(webFiles(files), sides.web, liveApi);
  return { started, page, results: mergeResults(tests, started.results, page.results) };
}

describe.skipIf(!hasDotnet)('authored FullStack challenges', () => {
  it('finds published FullStack challenges to check', () => expect(cases.length).toBeGreaterThan(0));

  it.each(cases)('$name', async ({ lesson }) => {
    const ch = lesson.challenge!;
    const tests: TestSpec[] = ch.tests.map((t, i) => ({ id: i + 1, ...t }));
    const failing = (results: TestResult[] | null) =>
      (results ?? []).filter((r) => !r.passed).map((r) => `${tests[r.testId - 1]?.label}: ${r.message}`);

    const solution = await runApp(ch.solutionFiles, tests);
    expect(solution.started.diagnostics.filter((d) => d.severity === 'error'), 'the solution API compiles').toEqual([]);
    expect(solution.results, 'results for every test').not.toBeNull();
    expect(failing(solution.results), 'solution should pass every test').toEqual([]);

    const starter = await runApp(ch.starterFiles, tests);
    if (process.env.CG_SHOW_STARTER) console.log(lesson.slug, failing(starter.results), starter.page.console);
    expect(failing(starter.results).length, 'starter should fail at least one test').toBeGreaterThan(0);

    const example = parseExampleFiles(lesson.exampleCode);
    expect(example, 'exampleCode is a JSON object of files').not.toBeNull();
    const ran = await runApp(example!, []);
    expect(ran.started.liveSession, `the example's API starts: ${ran.started.console.map((l) => l.text).join(' ')}`).toBeTruthy();
    expect(ran.page.console.filter((l) => l.level === 'error'), "the example's page has no errors").toEqual([]);
  }, 120_000);
});
