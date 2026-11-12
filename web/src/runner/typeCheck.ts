/**
 * Type checking for TypeScript lessons, shared by the app and the tests.
 *
 * Running and checking are separate, the way they are in a real Vite project: Sucrase strips the
 * types so the page runs (react.ts), and a type checker reports type errors. That lets a lesson say
 * "your code ran, but TypeScript spotted a mistake", which is the whole point of TypeScript.
 *
 * Two checkers implement `TypeChecker` with the same options (TS_COMPILER_OPTIONS), so what the tests
 * see is what the learner sees:
 *   - the app: Monaco's TypeScript worker (lesson/typeCheckMonaco.ts). It is the same TypeScript the
 *     editor uses for its red squiggles, and it's already downloaded on the lesson page.
 *   - Vitest:  the `typescript` package (typeCheckNode.ts), used by the content checks.
 * Both are TypeScript 5.9.
 */
import type { Files } from './compose';

/** One type error, in the learner's terms. Lines and columns start at 1. */
export interface TypeDiagnostic {
  file: string;
  line: number;
  column: number;
  /** TypeScript's error number, e.g. 2322. Lessons can test for a specific one. */
  code: number;
  message: string;
}

export type TypeChecker = (files: Files) => Promise<TypeDiagnostic[]>;

/**
 * Compiler options for learner code. Numbers rather than enum names so both checkers can use the
 * object as-is (Monaco's typings only list older enum members; the worker passes values straight to
 * TypeScript). No `lib`: target ES2022 brings in its default library, which includes the DOM.
 */
export const TS_COMPILER_OPTIONS = {
  strict: true, // what real projects use, and what makes TypeScript worth learning (no silent `any`)
  target: 9, // ES2022
  module: 99, // ESNext
  moduleResolution: 100, // Bundler: `import { x } from "./math"` finds math.ts, like Vite
  // Every file is a module, as it is at runtime (each file runs in its own scope). Without this, two
  // files without imports would share one global scope, and `let name` would clash with the DOM's
  // built-in `name`.
  moduleDetection: 3, // Force
  allowImportingTsExtensions: true,
  isolatedModules: true, // Sucrase compiles one file at a time, so flag code that needs more than that
  noEmit: true,
  noFallthroughCasesInSwitch: true,
  skipLibCheck: true,
  jsx: 1, // Preserve. TSX lessons will need React's types added to both checkers first.
} as const;

/** TypeScript error codes that beginners meet most, with a plain-words tip for each. */
const TIPS: Record<number, string> = {
  2322: "The value doesn't match the type it was given. Check the type after the colon, or the value.",
  2345: "The value you passed in doesn't match the type the function asks for.",
  2554: 'The function was called with the wrong number of values. Count the parameters.',
  7006: 'Add a type after the parameter name, like (name: string).',
  7031: 'Add a type for the values you unpack, like ({ name }: Pet).',
  2339: 'That property isn\'t part of the type. Check the spelling, or add it to the type.',
  2551: 'That property isn\'t part of the type. Check the spelling.',
  2304: 'TypeScript doesn\'t know that name. Check the spelling, or that it was made (or imported) first.',
  2741: 'The object is missing a property its type says it must have.',
  2739: 'The object is missing some properties its type says it must have.',
  2353: 'That property isn\'t in the type. Check the spelling, or add it to the type.',
  2531: 'This might be null. Check it first: if (thing) { ... }',
  18047: 'This might be null. Check it first: if (thing) { ... }',
  18048: 'This might be undefined. Check it first, or give it a default value.',
  2367: 'These two can never be equal, so this check is always false. Look at both types.',
  2564: 'Give this property a value in the constructor, or a starting value where it is declared.',
  2341: 'This property is private: only code inside the class can use it.',
  2420: "The class doesn't have everything the interface it implements asks for.",
  2355: 'A function with a return type must return a value on every path.',
  2366: 'A function with a return type must return a value on every path.',
  2459: 'That file has it, but doesn\'t share it. Put export in front of it in that file.',
  2305: 'That file doesn\'t export anything with that name. Check the spelling, and that it says export.',
  2307: 'TypeScript can\'t find that file. Check the file name after from, and start it with ./ (dot slash).',
  1005: 'Something is missing here, often a bracket, comma or semicolon.',
  1128: 'Something is missing here, often a bracket, comma or semicolon.',
};

/** "main.ts, line 3: Type 'string' is not assignable to type 'number'. Tip: …" */
export function describeTypeError(d: TypeDiagnostic, withTip = true): string {
  const tip = withTip && TIPS[d.code] ? ` Tip: ${TIPS[d.code]}` : '';
  return `${d.file}, line ${d.line}: ${d.message}${/[.!?]$/.test(d.message) ? '' : '.'}${tip}`;
}

/** The chained message shape both TypeScript and Monaco use. */
export interface MessageChain {
  messageText: string;
  next?: MessageChain[];
}

/**
 * TypeScript's long messages are chains ("Type '{…}' is not assignable to type 'Pet'." → "Property
 * 'age' is missing…"). Beginners get the first line plus the most specific line, not the whole tree.
 */
export function flattenMessage(text: string | MessageChain): string {
  if (typeof text === 'string') return text;
  let deepest: MessageChain = text;
  while (deepest.next?.[0]) deepest = deepest.next[0];
  return deepest === text ? text.messageText : `${text.messageText} ${deepest.messageText}`;
}

/** Learner files the checker should look at: .ts/.tsx, not dotfiles. */
export function typeScriptFiles(files: Files): string[] {
  return Object.keys(files).filter((f) => !f.startsWith('.') && /\.tsx?$/i.test(f));
}

/** Sorted by file order (as authored), then line, then column. Duplicates are dropped. */
export function sortDiagnostics(files: Files, list: TypeDiagnostic[]): TypeDiagnostic[] {
  const order = Object.keys(files);
  const seen = new Set<string>();
  return list
    .filter((d) => {
      const key = `${d.file}:${d.line}:${d.column}:${d.code}`;
      if (seen.has(key)) return false;
      seen.add(key);
      return true;
    })
    .sort((a, b) => order.indexOf(a.file) - order.indexOf(b.file) || a.line - b.line || a.column - b.column);
}
