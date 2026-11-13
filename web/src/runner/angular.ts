/**
 * Angular support for the preview (the runtime itself is vite-plugins/angularRuntime.ts).
 *
 * Angular code uses class decorators: `@Component({ ... }) export class App { }`. Browsers can't run
 * decorators yet, and Sucrase (which strips the types) passes them through untouched, so before
 * Sucrase runs, `lowerDecorators` rewrites each decorated class into plain JavaScript:
 *
 *   @Component({ selector: 'app-root',          (blank line)
 *     template: `<h1>Hi</h1>` })                (blank line)
 *   export class App {                          export class App {
 *     name = signal('Juni');                      name = signal('Juni');
 *   }                                           }; __cgDecorate(App, [Component({ selector: 'app-root', template: `<h1>Hi</h1>` })]);
 *
 * Line numbers stay true: the decorator's lines become blank lines, and the decorator call is joined
 * onto the line with the class's closing brace (its line breaks become spaces, or "\n" inside
 * template strings, so a multi-line template keeps its meaning). Angular's class decorators add
 * metadata to the class and return nothing, so calling them after the class is equivalent.
 *
 * Only class decorators are supported (@Component, @Injectable, @Pipe, @Directive). Modern Angular
 * replaces property decorators with functions (input(), output(), model(), viewChild(), inject()),
 * and those are what the lessons teach; a property decorator gets a friendly error pointing there.
 */
import type { Files } from './compose';

const CODE_FILE = /\.(tsx?|jsx?)$/i;
const IMPORTS_ANGULAR = /\bfrom\s*["']@angular\/|\bimport\s*["']@angular\//;

/**
 * True when any learner file imports Angular (so the page needs Angular's runtime and types). That
 * includes an inline <script type="text/typescript"> in index.html, which the worked examples use.
 */
export function needsAngular(files: Files): boolean {
  return Object.entries(files).some(
    ([name, source]) => !name.startsWith('.') && (CODE_FILE.test(name) || name === 'index.html') && IMPORTS_ANGULAR.test(source),
  );
}

/** A learner mistake found while lowering decorators, with the line it's on (1-based). */
export class DecoratorError extends SyntaxError {
  constructor(
    message: string,
    public readonly loc: { line: number; column: number },
  ) {
    super(message);
    this.name = 'SyntaxError';
  }
}

interface Token {
  start: number;
  end: number;
  /** 'name' | 'string' | 'template' | 'regex' | 'comment' | 'number' | 'punct' | 'space' */
  type: string;
  value: string;
}

/** Characters that can end an expression; after one of these, "/" is division, not a regex. */
const ENDS_EXPRESSION = /[\w$)\]}'"`]$/;
const KEYWORDS_BEFORE_EXPRESSION = new Set(['return', 'typeof', 'instanceof', 'in', 'of', 'new', 'delete', 'void', 'throw', 'case', 'do', 'else', 'yield', 'await']);

/**
 * Splits source into tokens, enough to find decorators and braces safely: strings, template literals
 * (with nested ${…}), comments and regex literals are single tokens, so braces inside them don't count.
 */
function tokenize(src: string): Token[] {
  const tokens: Token[] = [];
  let i = 0;
  let lastSignificant: Token | undefined;

  const push = (type: string, start: number, end: number) => {
    const t = { type, start, end, value: src.slice(start, end) };
    tokens.push(t);
    if (type !== 'space' && type !== 'comment') lastSignificant = t;
  };

  const readQuoted = (quote: string, from: number) => {
    let j = from + 1;
    while (j < src.length && src[j] !== quote && src[j] !== '\n') j += src[j] === '\\' ? 2 : 1;
    return Math.min(j + 1, src.length);
  };

  /** End of a template literal starting at `from` (the backtick), skipping nested ${ … } expressions. */
  const readTemplate = (from: number): number => {
    let j = from + 1;
    while (j < src.length) {
      const c = src[j];
      if (c === '\\') j += 2;
      else if (c === '`') return j + 1;
      else if (c === '$' && src[j + 1] === '{') {
        // Skip the expression, counting braces and stepping over strings/templates inside it.
        let depth = 1;
        j += 2;
        while (j < src.length && depth > 0) {
          const d = src[j];
          if (d === "'" || d === '"') j = readQuoted(d, j);
          else if (d === '`') j = readTemplate(j);
          else {
            if (d === '{') depth++;
            else if (d === '}') depth--;
            j++;
          }
        }
      } else j++;
    }
    return src.length;
  };

  while (i < src.length) {
    const c = src[i]!;
    if (/\s/.test(c)) {
      let j = i;
      while (j < src.length && /\s/.test(src[j]!)) j++;
      push('space', i, j);
      i = j;
    } else if (c === '/' && src[i + 1] === '/') {
      let j = i;
      while (j < src.length && src[j] !== '\n') j++;
      push('comment', i, j);
      i = j;
    } else if (c === '/' && src[i + 1] === '*') {
      const close = src.indexOf('*/', i + 2);
      const j = close < 0 ? src.length : close + 2;
      push('comment', i, j);
      i = j;
    } else if (c === "'" || c === '"') {
      const j = readQuoted(c, i);
      push('string', i, j);
      i = j;
    } else if (c === '`') {
      const j = readTemplate(i);
      push('template', i, j);
      i = j;
    } else if (c === '/' && !(lastSignificant && ENDS_EXPRESSION.test(lastSignificant.value) && !KEYWORDS_BEFORE_EXPRESSION.has(lastSignificant.value))) {
      // A regex literal: read to the closing slash (not inside a [class]), then its flags.
      let j = i + 1;
      let inClass = false;
      while (j < src.length && src[j] !== '\n') {
        if (src[j] === '\\') j += 2;
        else if (src[j] === '[') (inClass = true), j++;
        else if (src[j] === ']') (inClass = false), j++;
        else if (src[j] === '/' && !inClass) break;
        else j++;
      }
      j++;
      while (j < src.length && /[a-z]/i.test(src[j]!)) j++;
      push('regex', i, j);
      i = j;
    } else if (/[A-Za-z_$]/.test(c)) {
      let j = i;
      while (j < src.length && /[\w$]/.test(src[j]!)) j++;
      push('name', i, j);
      i = j;
    } else if (/\d/.test(c)) {
      let j = i;
      while (j < src.length && /[\w.]/.test(src[j]!)) j++;
      push('number', i, j);
      i = j;
    } else {
      push('punct', i, i + 1);
      i++;
    }
  }
  return tokens;
}

const lineOf = (src: string, index: number) => src.slice(0, index).split('\n').length;
const columnOf = (src: string, index: number) => index - src.lastIndexOf('\n', index - 1);

/** Replaces everything but line breaks with spaces, so lines below keep their numbers. */
const blankOut = (text: string) => text.replace(/[^\n]/g, ' ');

/** The decorator's source on one line: comments dropped, line breaks → spaces (or \n inside templates). */
function joinOnOneLine(tokens: Token[]): string {
  return tokens
    .filter((t) => t.type !== 'comment')
    .map((t) => {
      if (t.type === 'space') return t.value.includes('\n') ? ' ' : t.value;
      if (t.type === 'template') return t.value.replace(/\r?\n/g, '\\n');
      return t.value;
    })
    .join('');
}

/** A class field made with input(), model() or output(): [field name, kind, required]. */
export type SignalMember = [name: string, kind: 'input' | 'model' | 'output', required: boolean];

const SIGNAL_FUNCTIONS: Record<string, SignalMember[1]> = { input: 'input', model: 'model', output: 'output', outputFromObservable: 'output' };

/**
 * Finds `name = input(…)`, `name = input.required<T>(…)`, `name = model(…)` and `name = output<T>()`
 * fields directly in a class body. Angular's ahead-of-time compiler spots these by reading the code;
 * in the browser's JIT mode it can't, so the Angular CLI adds Input/Output metadata for them with a
 * build step. __cgDecorate does the same job (see GLUE in vite-plugins/angularRuntime.ts).
 */
function signalMembers(
  tokens: Token[],
  depthAt: number[],
  open: number,
  close: number,
  significant: (from: number, step: 1 | -1) => number,
): SignalMember[] {
  const found: SignalMember[] = [];
  for (let k = open + 1; k < close; k++) {
    const t = tokens[k]!;
    if (t.type !== 'punct' || t.value !== '=' || depthAt[k] !== 1) continue;
    const after = significant(k + 1, 1);
    const before = significant(k - 1, -1);
    if (after < 0 || before < 0) continue;
    // "==", "=>", "!=", "<=", "+=" and friends are operators, not a field's "=". ("name! = …" still counts.)
    if (t.end === tokens[after]!.start && ['=', '>'].includes(tokens[after]!.value)) continue;
    if (tokens[before]!.end === t.start && '=!<>+-*/%&|^?'.includes(tokens[before]!.value) && tokens[before]!.type === 'punct') continue;

    // Left side: `name`, `name!`, `name?` or `name: SomeType`.
    let nameAt = before;
    if (tokens[nameAt]!.value === '!' || tokens[nameAt]!.value === '?') nameAt = significant(nameAt - 1, -1);
    if (tokens[nameAt]?.type !== 'name') {
      // A type annotation: find the ":" on the same line, at the class body's depth.
      let colon = nameAt;
      while (colon > open && !(tokens[colon]!.value === ':' && depthAt[colon] === 1) && !tokens[colon]!.value.includes('\n')) colon--;
      if (tokens[colon]?.value !== ':') continue;
      nameAt = significant(colon - 1, -1);
      if (tokens[nameAt]!.value === '!' || tokens[nameAt]!.value === '?') nameAt = significant(nameAt - 1, -1);
      if (tokens[nameAt]?.type !== 'name') continue;
    }

    // Right side: input / input.required / model / output, an optional <Type>, then "(".
    const fn = tokens[after]!;
    const kind = fn.type === 'name' ? SIGNAL_FUNCTIONS[fn.value] : undefined;
    if (!kind) continue;
    let next = significant(after + 1, 1);
    let required = false;
    if (tokens[next]?.value === '.' && tokens[significant(next + 1, 1)]?.value === 'required') {
      required = true;
      next = significant(significant(next + 1, 1) + 1, 1);
    }
    if (tokens[next]?.value === '<') {
      let angle = 0;
      for (; next < close; next++) {
        if (tokens[next]!.value === '<') angle++;
        else if (tokens[next]!.value === '>' && tokens[next - 1]?.value !== '=') angle--;
        if (angle === 0) break;
      }
      next = significant(next + 1, 1);
    }
    if (tokens[next]?.value !== '(') continue;
    found.push([tokens[nameAt]!.value, kind, required]);
  }
  return found;
}

/**
 * Rewrites decorated classes into plain JavaScript (see the top of this file). Code without
 * decorators comes back unchanged. Throws a DecoratorError, with the line, for decorators that
 * Codegrove doesn't support.
 */
export function lowerDecorators(src: string, fileName: string): string {
  if (!src.includes('@')) return src;
  const tokens = tokenize(src);
  const significant = (from: number, step: 1 | -1) => {
    for (let k = from; k >= 0 && k < tokens.length; k += step) if (tokens[k]!.type !== 'space' && tokens[k]!.type !== 'comment') return k;
    return -1;
  };
  const fail = (index: number, message: string): never => {
    throw new DecoratorError(`${fileName}, line ${lineOf(src, index)}: ${message}`, { line: lineOf(src, index), column: columnOf(src, index) });
  };

  // Brace depth before each token, so we know whether a decorator sits on a class or inside one.
  const depthAt: number[] = [];
  let depth = 0;
  for (const t of tokens) {
    depthAt.push(depth);
    if (t.type === 'punct' && (t.value === '{' || t.value === '(' || t.value === '[')) depth++;
    if (t.type === 'punct' && (t.value === '}' || t.value === ')' || t.value === ']')) depth--;
  }

  /** Index of the token that closes the bracket opened at `open`. */
  const matching = (open: number): number => {
    const pairs: Record<string, string> = { '(': ')', '{': '}', '[': ']' };
    const want = pairs[tokens[open]!.value]!;
    for (let k = open + 1; k < tokens.length; k++) {
      if (tokens[k]!.type === 'punct' && tokens[k]!.value === want && depthAt[k] === depthAt[open]! + 1) return k;
    }
    return fail(tokens[open]!.start, `This ${tokens[open]!.value} is never closed. Look for a missing ${want}.`);
  };

  interface Rewrite {
    /** Source ranges to blank out (the decorators). */
    blanks: [number, number][];
    /** Where to add the decorator call: just after the class's closing brace. */
    insertAt: number;
    insert: string;
  }
  const rewrites: Rewrite[] = [];

  for (let k = 0; k < tokens.length; k++) {
    const t = tokens[k]!;
    if (t.type !== 'punct' || t.value !== '@') continue;
    const nameIndex = significant(k + 1, 1);
    if (nameIndex < 0 || tokens[nameIndex]!.type !== 'name') continue; // not a decorator (e.g. "@" in odd code)

    if (depthAt[k] !== 0) {
      const name = tokens[nameIndex]!.value;
      const modern: Record<string, string> = {
        Input: 'input()',
        Output: 'output()',
        ViewChild: 'viewChild()',
        ViewChildren: 'viewChildren()',
        ContentChild: 'contentChild()',
        ContentChildren: 'contentChildren()',
        HostListener: "the host property of @Component, like host: { '(click)': 'onClick()' }",
        HostBinding: "the host property of @Component, like host: { '[class.active]': 'active()' }",
        Inject: 'inject()',
      };
      const instead = modern[name];
      fail(
        t.start,
        `@${name} is a decorator inside a class. In Codegrove, decorators only go on classes (like @Component).` +
          (instead ? ` Use ${instead} instead: it's the modern way to do the same thing.` : ''),
      );
    }

    // Read one or more decorators in a row: @Name, @Name.member, each optionally with (arguments).
    const blanks: [number, number][] = [];
    const calls: string[] = [];
    let cursor = k;
    while (cursor >= 0 && tokens[cursor]!.type === 'punct' && tokens[cursor]!.value === '@') {
      let end = significant(cursor + 1, 1);
      if (end < 0 || tokens[end]!.type !== 'name') fail(tokens[cursor]!.start, 'A decorator needs a name after the @, like @Component.');
      for (;;) {
        const dot = significant(end + 1, 1);
        if (dot >= 0 && tokens[dot]!.value === '.' && tokens[significant(dot + 1, 1)]?.type === 'name') end = significant(dot + 1, 1);
        else break;
      }
      const paren = significant(end + 1, 1);
      if (paren >= 0 && tokens[paren]!.value === '(') end = matching(paren);
      const start = tokens[cursor]!.start;
      blanks.push([start, tokens[end]!.end]);
      calls.push(joinOnOneLine(tokens.slice(cursor + 1, end + 1)));
      cursor = significant(end + 1, 1);
    }

    // Then the class: [export] [default] [abstract] class Name … {
    let head = cursor;
    const modifiers: string[] = [];
    while (head >= 0 && ['export', 'default', 'abstract', 'declare'].includes(tokens[head]!.value)) {
      modifiers.push(tokens[head]!.value);
      head = significant(head + 1, 1);
    }
    if (head < 0 || tokens[head]!.value !== 'class') {
      fail(blanks[0]![0], 'A decorator like @Component must sit right above a class, like: @Component({ ... }) export class App { }');
    }
    const nameIndex2 = significant(head + 1, 1);
    if (nameIndex2 < 0 || tokens[nameIndex2]!.type !== 'name' || tokens[nameIndex2]!.value === 'extends' || tokens[nameIndex2]!.value === 'implements') {
      fail(tokens[head]!.start, 'Give the class a name, like: export class App { }');
    }
    const className = tokens[nameIndex2]!.value;
    let open = nameIndex2;
    while (open < tokens.length && !(tokens[open]!.value === '{' && depthAt[open] === 0)) open++;
    if (open >= tokens.length) fail(tokens[head]!.start, `The class ${className} needs a body in braces: { }`);
    const close = matching(open);
    const members = signalMembers(tokens, depthAt, open, close, significant);

    rewrites.push({
      blanks,
      insertAt: tokens[close]!.end,
      insert: ` __cgDecorate(${className}, [${calls.join(', ')}]${members.length ? `, ${JSON.stringify(members)}` : ''});`,
    });
    // Carry on from the class keyword: the decorators just read are done, and any decorator inside the
    // class body is then found at depth > 0 and explained.
    k = head;
  }

  if (rewrites.length === 0) return src;
  let out = '';
  let last = 0;
  for (const r of rewrites) {
    for (const [from, to] of r.blanks) {
      out += src.slice(last, from) + blankOut(src.slice(from, to));
      last = to;
    }
    out += src.slice(last, r.insertAt) + r.insert;
    last = r.insertAt;
  }
  return out + src.slice(last);
}

/**
 * Timing for Angular pages. The harness also waits while window.__cgBusy() says Angular is still
 * starting up or re-rendering (see GLUE in vite-plugins/angularRuntime.ts), so these are just short
 * pauses on top of that.
 */
export const ANGULAR_RUN_OPTIONS = { settleMs: 40, stepDelayMs: 20 } as const;
