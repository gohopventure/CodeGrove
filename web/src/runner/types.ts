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
  | 'typeError'; // { file?, code?, contains?, line? }: TypeScript DOES report a matching error

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
  | { type: 'cg:done'; runId: string; results: TestResult[] };
