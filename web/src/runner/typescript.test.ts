/**
 * TypeScript pages: types are stripped so the code runs (like Vite), and the type checker's findings
 * reach the typeCheck / typeError tests and the console. Uses the test-only checker (typeCheckNode.ts),
 * which shares compiler options with the app's Monaco checker.
 */
import { describe, expect, it } from 'vitest';
import { composeFiles } from './compose';
import { hasTypeScript, loadToolkit, needsReact, needsReactRuntime } from './react';
import { runInJsdom } from './runInJsdom';
import { describeTypeError, flattenMessage } from './typeCheck';
import { nodeTypeChecker } from './typeCheckNode';
import type { TestKind, TestSpec } from './types';

const tests = (...list: [TestKind, Record<string, unknown>][]): TestSpec[] =>
  list.map(([kind, spec], i) => ({ id: i + 1, label: `t${i + 1}`, kind, spec, failMessage: `fail ${i + 1}` }));

const html = (entry = 'main.ts') =>
  `<!DOCTYPE html><html><head><title>T</title></head><body><p id="out"></p><script type="module" src="${entry}"></script></body></html>`;

describe('TypeScript detection', () => {
  it('compiles .ts pages without loading React', () => {
    const files = { 'main.ts': 'const n: number = 1;', 'index.html': html() };
    expect(needsReact(files)).toBe(true);
    expect(hasTypeScript(files)).toBe(true);
    expect(needsReactRuntime(files)).toBe(false);
  });

  it('still loads React for .tsx files and for imports of react', () => {
    expect(needsReactRuntime({ 'App.tsx': 'export default 1;' })).toBe(true);
    expect(needsReactRuntime({ 'main.ts': 'import { useState } from "react";' })).toBe(true);
  });

  it('leaves .d.ts files and dotfiles out', () => {
    expect(needsReact({ 'index.html': '<p></p>', 'globals.d.ts': 'declare const x: number;' })).toBe(false);
    expect(hasTypeScript({ '.blocks.json': '{}', 'index.html': '' })).toBe(false);
  });

  it('composes a TS-only page with no React runtime in it', async () => {
    const kit = await loadToolkit({ react: false });
    const page = composeFiles({ 'main.ts': 'const n: number = 1;\nconsole.log(n);', 'index.html': html() }, kit);
    expect(page).toContain('__cgStart("main.ts")');
    expect(page).not.toContain('__cgDefine("react"'); // React's modules aren't registered
    expect(page.length).toBeLessThan(20_000); // React's runtime alone is ~1.3 MB
    expect(page).toMatch(/__cgDefine\("main\.ts", function \(module, exports, require\) \{.*const n = 1;/); // types stripped
  });
});

describe('nodeTypeChecker', () => {
  it('reports nothing for correct code', async () => {
    expect(await nodeTypeChecker({ 'main.ts': 'let lives: number = 3;\nlives = lives - 1;\n' })).toEqual([]);
  });

  it('reports a type error with file, line and code', async () => {
    const [d, ...rest] = await nodeTypeChecker({ 'main.ts': 'let lives: number = 3;\nlives = "three";\n' });
    expect(rest).toEqual([]);
    expect(d).toMatchObject({ file: 'main.ts', line: 2, code: 2322 });
    expect(describeTypeError(d!)).toMatch(/^main\.ts, line 2: Type 'string' is not assignable to type 'number'\. Tip: /);
  });

  it('is strict: parameters need types', async () => {
    const found = await nodeTypeChecker({ 'main.ts': 'function greet(name) { return "Hi " + name; }\ngreet("Sam");\n' });
    expect(found.map((d) => d.code)).toEqual([7006]);
  });

  it('treats each file as a module, so names don\'t clash with the DOM or each other', async () => {
    const files = { 'main.ts': 'const name = "Sam";\nconsole.log(name);\n', 'other.ts': 'const name = 5;\nconsole.log(name);\n' };
    expect(await nodeTypeChecker(files)).toEqual([]);
  });

  it('resolves imports between lesson files without extensions, like Vite', async () => {
    const files = {
      'main.ts': 'import { double } from "./math";\nimport type { Pet } from "./types";\nconst p: Pet = { name: "Rex", age: double(2) };\nconsole.log(p);\n',
      'math.ts': 'export function double(n: number): number { return n * 2; }\n',
      'types.ts': 'export interface Pet { name: string; age: number }\n',
    };
    expect(await nodeTypeChecker(files)).toEqual([]);
    const broken = { ...files, 'main.ts': files['main.ts'].replace('double(2)', 'double("2")') };
    expect((await nodeTypeChecker(broken)).map((d) => [d.file, d.line, d.code])).toEqual([['main.ts', 3, 2345]]);
  });

  it('knows the DOM: querySelector can return null', async () => {
    const found = await nodeTypeChecker({ 'main.ts': 'const out = document.querySelector("#out");\nout.textContent = "hi";\n' });
    expect(found.map((d) => d.code)).toEqual([18047]);
  });
});

describe('flattenMessage', () => {
  it('keeps the first line and the most specific line of a chain', () => {
    const chain = { messageText: 'A is not assignable to B.', next: [{ messageText: 'Middle.', next: [{ messageText: "Property 'age' is missing." }] }] };
    expect(flattenMessage(chain)).toBe("A is not assignable to B. Property 'age' is missing.");
    expect(flattenMessage('plain')).toBe('plain');
  });
});

describe('TypeScript pages in the harness', () => {
  it('runs code with its types stripped, across files', async () => {
    const files = {
      'main.ts': 'import { label } from "./label";\nconst out = document.querySelector<HTMLParagraphElement>("#out")!;\nout.textContent = label("Rex", 3);\n',
      'label.ts': 'export function label(name: string, age: number): string {\n  return `${name} is ${age}`;\n}\n',
      'index.html': html(),
    };
    const { results, console } = await runInJsdom(files, tests(['elementText', { selector: '#out', equals: 'Rex is 3' }], ['typeCheck', {}], ['noErrors', {}]));
    expect(results.map((r) => r.passed)).toEqual([true, true, true]);
    expect(console).toEqual([]);
  });

  it('still runs code that has type errors, and reports them', async () => {
    const files = { 'main.ts': 'let score: number = 10;\nscore = "ten";\nconsole.log("score is", score);\n', 'index.html': html() };
    const { results, console } = await runInJsdom(
      files,
      tests(
        ['consoleIncludes', { text: 'score is ten' }],
        ['noErrors', {}], // a type error isn't a runtime error
        ['typeCheck', {}],
        ['typeError', { code: 2322, line: 2 }],
        ['typeError', { contains: "not assignable to type 'number'" }],
        ['typeError', { code: 2345 }],
      ),
    );
    expect(results.map((r) => r.passed)).toEqual([true, true, false, true, true, false]);
    expect(results[2]!.message).toMatch(/^fail 3 First one: main\.ts, line 2: Type 'string' is not assignable/);
    expect(console[0]).toMatchObject({ level: 'warn' });
    expect(console[0]!.text).toMatch(/^Type error in main\.ts, line 2: /);
  });

  it('can limit typeCheck to one file', async () => {
    const files = { 'main.ts': 'export const ok: number = 1;\n', 'scratch.ts': 'export const bad: number = "x";\n', 'index.html': html() };
    const { results } = await runInJsdom(files, tests(['typeCheck', { file: 'main.ts' }], ['typeCheck', { file: 'scratch.ts' }]));
    expect(results.map((r) => r.passed)).toEqual([true, false]);
  });

  // jsdom ignores //# sourceURL, so the "(main.ts, line 3)" part is checked in the browser end-to-end
  // run; here we check the error is reported at all.
  it('reports runtime errors in .ts files', async () => {
    const files = { 'main.ts': 'const pets: string[] = [];\n\nconsole.log(pets[0]!.toUpperCase());\n', 'index.html': html() };
    const { results, console } = await runInJsdom(files, tests(['noErrors', {}]));
    expect(results[0]!.passed).toBe(false);
    expect(console.find((l) => l.level === 'error')?.text).toMatch(/^TypeError: .*toUpperCase/);
  });

  it('runs an inline <script type="text/typescript">', async () => {
    const page = `<!DOCTYPE html><html><body><p id="out"></p><script type="text/typescript">
      const n: number = 6 * 7;
      document.querySelector("#out")!.textContent = String(n);
    </script></body></html>`;
    const { results } = await runInJsdom({ 'index.html': page }, tests(['elementText', { selector: '#out', equals: '42' }]));
    expect(results[0]!.passed).toBe(true);
  });

  it('fails type tests when nothing was checked', async () => {
    // A plain JS page has no TypeScript, so no diagnostics are handed over.
    const { results } = await runInJsdom({ 'index.html': '<p>hi</p>' }, tests(['typeCheck', {}]));
    expect(results[0]).toMatchObject({ passed: false });
    expect(results[0]!.message).toMatch(/couldn't check/);
  });
});
