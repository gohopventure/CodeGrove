/** One declarative test, exactly as the API sends it. Authors write these in JSON; no code. */
export interface TestSpec {
  id: number;
  label: string;
  kind: TestKind;
  spec: Record<string, unknown>;
  failMessage: string;
}

export type TestKind =
  | 'elementExists' // { selector }
  | 'elementCount' // { selector, min?, max? }
  | 'elementText' // { selector, equals?, contains?, notEquals?, minLength?, caseSensitive? }
  | 'attribute' // { selector, name, equals?, notEmpty? }
  | 'cssProperty' // { selector, property, equals }: the computed value on the element
  | 'cssRule' // { selector, property, equals?, matches?, media? }: the value the learner's CSS declares
  | 'jsExpression' // { expression, equals }
  | 'interact' // { selector, click?, type? }: clicks and/or types, so later tests can check the result
  | 'consoleIncludes' // { text }
  | 'noErrors' // {}
  | 'typeCheck' // { file? }: TypeScript found no type errors (in that file, or in any file)
  | 'typeError' // { file?, code?, contains?, line? }: TypeScript DOES report a matching error
  // C# only (evaluated by the C# runner, src/Codegrove.CSharpRunner/TestEvaluator.cs). consoleIncludes and
  // noErrors work for C# too.
  | 'consoleEquals' // { lines }: the program's whole output is exactly these lines
  | 'compileError' // { code?, contains?, line?, file? }: the compiler DOES report a matching error
  | 'compileWarning' // { code?, contains?, line?, file? }: the compiler DOES report a matching warning
  | 'noWarnings' // { code?, file? }: it compiles with no warnings (or none whose code starts with code, e.g. "CS86")
  | 'csharpExpression' // { expression, equals }: a C# expression run after the program, against the learner's classes
  | 'httpRequest' // { method?, path, body?, before?, expectStatus?, expectJson?, expectBody?, expectHeader? … }: the learner's web server answers as expected (WebChecks.cs). C# only.
  | 'sourceMatches' // { pattern, file?, ignoreCase?, min?, max? }: the code (comments removed) matches a regex (max: 0 = must not). C# and SQL.
  // SQL only (evaluated by src/runner/sqlEngine.ts). noErrors and sourceMatches work for SQL too.
  | 'sqlResultEquals' // { rows, columns?, ordered?, sortedBy?, caseSensitive?, result? }: a result set has exactly these rows (ordered = this exact order; sortedBy = sorted by these columns, ties in any order)
  | 'sqlRowCount' // { equals?, min?, max?, result? }: how many rows a result set has
  | 'sqlCheckQuery' // { query, equals, ordered?, caseSensitive?, missing?, report? }: a hidden query run after the learner's code (equals: a value or rows)
  | 'sqlError'; // { contains? }: the learner's code DOES stop with a matching error

export interface TestResult {
  testId: number;
  passed: boolean;
  message: string | null;
}

export interface ConsoleLine {
  level: 'log' | 'info' | 'warn' | 'error';
  text: string;
}

/** Messages the preview frame posts to the app. Every one carries the runId it belongs to. */
export type RunnerMessage =
  | { type: 'cg:ready' }
  | { type: 'cg:console'; runId: string; level: ConsoleLine['level']; text: string }
  | { type: 'cg:done'; runId: string; results: TestResult[] }
  /** A capstone page called its API (mockApi.ts LiveFetchMessage). */
  | { type: 'cg:fetch'; runId: string }
  /** A picture of the page for the portfolio (snapshot.ts SnapshotReply). */
  | { type: 'cg:snapshot-result'; runId: string; id: string; dataUrl?: string; error?: string };
