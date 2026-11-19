/*
 * The two functions in this file run INSIDE the sandboxed preview frame. They are turned into text
 * with Function.prototype.toString() and embedded in the composed document, so each must be fully
 * self-contained: no imports, no references to anything outside its own body and parameters.
 * They're also imported normally by unit tests, which run them against jsdom.
 */
import type { ConsoleLine, TestResult, TestSpec } from './types';

/** Evaluates one declarative test against the learner's rendered page. */
export function evaluateTest(test: TestSpec, doc: Document, win: Window, lines: ConsoleLine[]): TestResult {
  const s = test.spec as Record<string, any>;
  const norm = (v: unknown, caseSensitive: boolean) => {
    const text = String(v ?? '').replace(/\s+/g, ' ').trim();
    return caseSensitive ? text : text.toLowerCase();
  };
  const caseSensitive = s.caseSensitive === true;
  let passed = false;

  switch (test.kind) {
    case 'elementExists':
      passed = doc.querySelector(s.selector) !== null;
      break;

    case 'elementCount': {
      const n = doc.querySelectorAll(s.selector).length;
      passed = (s.min === undefined || n >= s.min) && (s.max === undefined || n <= s.max);
      break;
    }

    case 'elementText': {
      const el = doc.querySelector(s.selector);
      if (!el) break;
      const text = norm(el.textContent, caseSensitive);
      passed =
        (s.equals === undefined || text === norm(s.equals, caseSensitive)) &&
        (s.contains === undefined || text.includes(norm(s.contains, caseSensitive))) &&
        (s.notEquals === undefined || text !== norm(s.notEquals, caseSensitive)) &&
        (s.minLength === undefined || text.length >= s.minLength);
      break;
    }

    case 'attribute': {
      const el = doc.querySelector(s.selector);
      const value = el ? el.getAttribute(s.name) : null;
      passed =
        value !== null &&
        (s.equals === undefined || value.trim() === String(s.equals)) &&
        (s.notEmpty !== true || value.trim().length > 0);
      break;
    }

    case 'cssProperty': {
      const el = doc.querySelector(s.selector);
      if (!el) break;
      const actual = win.getComputedStyle(el).getPropertyValue(s.property).trim();
      const wanted = String(s.equals).trim();
      if (actual === wanted) {
        passed = true;
        break;
      }
      // Let the browser normalize the expected value too ("purple" → "rgb(128, 0, 128)").
      const probe = doc.createElement('div');
      probe.style.setProperty(s.property, wanted);
      // A border width only computes when the border has a style; otherwise it's always 0px.
      if (/^border(-(top|right|bottom|left))?-width$/.test(s.property)) probe.style.borderStyle = 'solid';
      doc.body.appendChild(probe);
      const expected = win.getComputedStyle(probe).getPropertyValue(s.property).trim();
      probe.remove();
      passed = actual === expected && expected !== '';
      break;
    }

    case 'cssRule': {
      // Reads what the learner's stylesheet DECLARES for the element (last matching rule wins;
      // specificity is ignored). Used for things computed styles can't show reliably: grid track
      // lists, and rules that only apply inside a media query (pass `media`, e.g. "max-width: 600px").
      const el = doc.querySelector(s.selector);
      if (!el) break;
      const wantMedia = s.media ? String(s.media).replace(/\s+/g, '').toLowerCase() : null;
      let declared: string | null = null;
      const visit = (rules: CSSRuleList, inMedia: boolean) => {
        for (const rule of Array.from(rules) as any[]) {
          if (rule.media && rule.cssRules) {
            const condition = String(rule.media.mediaText ?? '').replace(/\s+/g, '').toLowerCase();
            if (wantMedia && condition.includes(wantMedia)) visit(rule.cssRules, true);
            continue;
          }
          if (!rule.selectorText || inMedia !== !!wantMedia) continue;
          let matches = false;
          try {
            matches = el.matches(rule.selectorText);
          } catch {
            matches = false;
          }
          const value = matches ? String(rule.style.getPropertyValue(s.property) ?? '').trim() : '';
          if (value) declared = value;
        }
      };
      for (const sheet of Array.from(doc.styleSheets)) {
        try {
          visit(sheet.cssRules, false);
        } catch {
          // cross-origin sheets can't be read; learners' sheets are always inline
        }
      }
      const value = declared as string | null;
      passed =
        value !== null &&
        (s.equals === undefined || norm(value, caseSensitive) === norm(s.equals, caseSensitive)) &&
        (s.matches === undefined || new RegExp(String(s.matches), 'i').test(value));
      break;
    }

    case 'jsExpression': {
      // The expression is authored content, evaluated in the learner's sandboxed page.
      const value = new (win as any).Function(`return (${s.expression});`)();
      passed = JSON.stringify(value) === JSON.stringify(s.equals);
      break;
    }

    case 'interact': {
      // Does something a learner would do, so later tests can check what changed. Tests run in order
      // and (for React pages) with a short pause after each one, so the page has re-rendered by the
      // time the next test looks. Passes when the element exists and the action ran without an error.
      //   { selector, click: true }  or  { selector, type: "Sam" }  (both may be given: click first)
      const el = doc.querySelector(s.selector) as HTMLElement | null;
      if (!el) break;
      if (s.click === true) el.click();
      if (typeof s.type === 'string') {
        const field = el as HTMLInputElement | HTMLTextAreaElement;
        // Set the value the way a keystroke does: through the element prototype's setter, so libraries
        // that track the "last known value" (React does) see a real change, then fire input + change.
        const proto = Object.getPrototypeOf(field);
        const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
        if (setter) setter.call(field, s.type);
        else field.value = s.type;
        field.dispatchEvent(new (win as any).Event('input', { bubbles: true }));
        field.dispatchEvent(new (win as any).Event('change', { bubbles: true }));
      }
      passed = true;
      break;
    }

    case 'consoleIncludes':
      passed = lines.some((l) => l.level !== 'error' && norm(l.text, caseSensitive).includes(norm(s.text, caseSensitive)));
      break;

    case 'noErrors':
      passed = !lines.some((l) => l.level === 'error');
      break;

    case 'typeCheck':
    case 'typeError': {
      // Type errors are found before the page runs (typeCheck.ts) and handed to the harness, which
      // puts them on window.__cgTypeDiagnostics. Missing means the checker didn't run: fail.
      const all = (win as any).__cgTypeDiagnostics as
        | { file: string; line: number; column: number; code: number; message: string }[]
        | undefined;
      if (!Array.isArray(all)) {
        return { testId: test.id, passed: false, message: `${test.failMessage} (TypeScript couldn't check your code this time. Try again.)` };
      }
      const inFile = s.file ? all.filter((d) => d.file === s.file) : all;
      if (test.kind === 'typeCheck') {
        const first = inFile[0];
        if (!first) return { testId: test.id, passed: true, message: null };
        const more = inFile.length > 1 ? ` (and ${inFile.length - 1} more)` : '';
        return {
          testId: test.id,
          passed: false,
          message: `${test.failMessage} First one: ${first.file}, line ${first.line}: ${first.message}${more}`,
        };
      }
      passed = inFile.some(
        (d) =>
          (s.code === undefined || d.code === s.code) &&
          (s.line === undefined || d.line === s.line) &&
          (s.contains === undefined || d.message.toLowerCase().includes(String(s.contains).toLowerCase())),
      );
      break;
    }

    default:
      passed = false;
  }

  return { testId: test.id, passed, message: passed ? null : test.failMessage };
}

interface HarnessConfig {
  runId: string;
  tests: TestSpec[];
  settleMs: number;
  /** Pause after each test (React pages), so clicks and typing have re-rendered before the next test. */
  stepDelayMs?: number;
  /** TypeScript pages: the type errors found before the run, as display text plus data for the tests. */
  typeErrors?: { text: string; diagnostic: { file: string; line: number; column: number; code: number; message: string } }[];
}

/**
 * First script in the composed page. Captures console output and errors, waits for the page to
 * load and settle, runs the tests and posts the results to the app.
 */
export function harnessMain(cfg: HarnessConfig, evaluate: typeof evaluateTest): void {
  const lines: ConsoleLine[] = [];
  const post = (msg: Record<string, unknown>) => window.parent.postMessage({ ...msg, runId: cfg.runId }, '*');
  // Where an error happened, in the learner's terms. React and Angular lessons compile each file into
  // its own script named with //# sourceURL, so the stack names the file ("App.jsx:7:12",
  // "pet.service.ts:5:3"); the first such frame wins, which skips the framework's own frames.
  const learnerFile = /([\w.-]+\.(?:jsx?|tsx?)):(\d+):\d+/;
  const locate = (stack: unknown) => {
    // Angular's template compiler names its generated code "ng:///App.js"; that's never the learner's file.
    const frame = learnerFile.exec(String(stack ?? '').replace(/ng:\/\/\/[^\s)]*/g, ''));
    return frame ? ` (${frame[1]}, line ${frame[2]})` : '';
  };

  const format = (v: unknown) => {
    if (typeof v === 'string') return v;
    // Error objects (console.error(err), Angular's "ERROR" logs) would otherwise print as "{}".
    if (v instanceof Error) {
      const located = /^[\w.-]+\.(?:jsx?|tsx?), line \d+:/.test(v.message);
      return `${located ? '' : `${v.name}: `}${v.message}${located ? '' : locate(v.stack)}`;
    }
    try {
      return JSON.stringify(v);
    } catch {
      return String(v);
    }
  };
  const record = (level: ConsoleLine['level'], text: string) => {
    lines.push({ level, text });
    post({ type: 'cg:console', level, text });
  };

  // Browser extensions (password managers, wallets, writing assistants…) inject scripts into every
  // frame, ours included, and some of them log. Only the learner's code should reach the learner's
  // console, so anything whose call stack or source file belongs to an extension is ignored.
  const fromExtension = (text: unknown) =>
    typeof text === 'string' && /(?:chrome|moz|safari-web|ms-browser)-extension:\/\/|webkit-masked-url:/i.test(text);

  // console.log("%s has %d lives", name, 3) works like the browser's console: placeholders take the
  // next arguments (%c styling is dropped). React's warnings are written this way.
  const formatArgs = (args: unknown[]) => {
    if (typeof args[0] !== 'string' || !args[0].includes('%')) return args.map(format).join(' ');
    const rest = args.slice(1);
    const head = args[0].replace(/%([sdifoOc%])/g, (whole, kind: string) => {
      if (kind === '%') return '%';
      if (rest.length === 0) return whole;
      const value = rest.shift();
      if (kind === 'c') return '';
      if (kind === 'd' || kind === 'i') return String(parseInt(String(value), 10));
      if (kind === 'f') return String(parseFloat(String(value)));
      return format(value);
    });
    return [head, ...rest.map(format)].join(' ').trim();
  };

  (['log', 'info', 'warn', 'error'] as const).forEach((level) => {
    const original = console[level].bind(console);
    console[level] = (...args: unknown[]) => {
      if (!fromExtension(new Error().stack)) record(level, formatArgs(args));
      original(...args);
    };
  });

  const where = (e: ErrorEvent) => locate(e.error?.stack) || locate(`${e.filename ?? ''}:${e.lineno}:0`) || (e.lineno ? ` (line ${e.lineno})` : '');

  // TypeScript pages: the type errors go to the console panel first, so the learner sees what TypeScript
  // found before anything their code logs. They're posted but not added to `lines`: they aren't
  // runtime errors, so noErrors and consoleIncludes ignore them (typeCheck/typeError test them).
  if (cfg.typeErrors) {
    Object.defineProperty(window, '__cgTypeDiagnostics', {
      value: Object.freeze(cfg.typeErrors.map((t) => Object.freeze(t.diagnostic))),
      writable: false,
      configurable: false,
    });
    cfg.typeErrors.forEach((t) => post({ type: 'cg:console', level: 'warn', text: `Type error in ${t.text}` }));
  }

  window.addEventListener('error', (e) => {
    if (fromExtension(e.filename) || fromExtension(e.error?.stack)) return;
    record('error', e.message + where(e));
  });

  // Keep the preview on the learner's page: links to other pages don't leave the sandbox (it
  // couldn't load them anyway); the console explains what would have happened.
  document.addEventListener(
    'click',
    (e) => {
      const link = (e.target as Element | null)?.closest?.('a[href]');
      const href = link?.getAttribute('href') ?? '';
      if (link && !href.startsWith('#')) {
        e.preventDefault();
        record('info', `Link to ${href} (links to other pages don't open in the preview, but they will on a real website)`);
      }
    },
    true,
  );

  // Forms. The frame is sandboxed without allow-forms, and in such a frame browsers never fire
  // "submit" at all, so a learner's form.addEventListener('submit', ...) would never run. Instead,
  // once a click on a submit button has finished (this listener is on window, so it runs after the
  // learner's own click handlers) the harness cancels the native submission and fires a synthetic
  // submit event that behaves like the real one: it bubbles, it's cancelable and it has a submitter.
  // Nothing can leave the frame either way (sandbox + CSP form-action 'none').
  window.addEventListener('click', (e) => {
    const submitter = (e.target as Element | null)?.closest?.('button:not([type]), button[type="submit"], input[type="submit"]') as
      | HTMLButtonElement
      | HTMLInputElement
      | null;
    const form = submitter?.form;
    if (!form) return;
    const canceledByLearner = e.defaultPrevented;
    e.preventDefault();
    if (canceledByLearner) return;
    const init = { bubbles: true, cancelable: true };
    form.dispatchEvent(typeof SubmitEvent === 'function' ? new SubmitEvent('submit', { ...init, submitter }) : new Event('submit', init));
  });

  // A submit the learner's code didn't handle (no listener, or no preventDefault) is explained in the console.
  window.addEventListener('submit', (e) => {
    if (e.defaultPrevented) return;
    e.preventDefault();
    record('info', 'Form submitted! (In the preview, forms don\'t send anything anywhere.)');
  });
  window.addEventListener('unhandledrejection', (e) => {
    if (fromExtension(e.reason?.stack)) return;
    record('error', `Promise error: ${format(e.reason)}`);
  });

  const runOne = (t: TestSpec): TestResult => {
    try {
      return evaluate(t, document, window, lines);
    } catch {
      return { testId: t.id, passed: false, message: t.failMessage };
    }
  };

  // Practice API requests (mockApi.ts) still in flight. Pages that fetch are tested once their requests
  // have answered, so the learner's .then() callbacks (and React's re-render) have run.
  const pendingRequests = () => (window as Window & { __cgMockPending?: number }).__cgMockPending ?? 0;
  // Frameworks that render asynchronously can say they're still busy. Angular pages define this
  // (vite-plugins/angularRuntime.ts): true while the app starts up or re-renders after a click.
  const frameworkBusy = () => {
    try {
      return (window as Window & { __cgBusy?: () => boolean }).__cgBusy?.() === true;
    } catch {
      return false;
    }
  };

  /** Calls `then` once no practice-API request is in flight and the framework is idle (capped at 2 s), plus `pauseMs`. */
  const afterIdle = (pauseMs: number, then: () => void) => {
    const started = Date.now();
    const poll = () => {
      if ((pendingRequests() > 0 || frameworkBusy()) && Date.now() - started < 2000) setTimeout(poll, 20);
      else setTimeout(then, pauseMs);
    };
    poll();
  };

  const runTests = () => {
    const delay = cfg.stepDelayMs ?? 0;
    if (delay <= 0) {
      post({ type: 'cg:done', results: cfg.tests.map(runOne) });
      return;
    }
    // One test at a time. After each, wait for any request it started (a click that fetches) and
    // then pause, so the re-render it triggered has landed before the next test reads the page.
    const results: TestResult[] = [];
    const next = (i: number) => {
      if (i >= cfg.tests.length) {
        post({ type: 'cg:done', results });
        return;
      }
      results.push(runOne(cfg.tests[i]!));
      afterIdle(delay, () => next(i + 1));
    };
    next(0);
  };

  // After "load" and the settle time: wait for requests made on load, then (for React pages) one
  // more pause for the re-render their data causes.
  window.addEventListener('load', () => setTimeout(() => afterIdle(cfg.stepDelayMs ?? 0, runTests), cfg.settleMs));
}
