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

    case 'consoleIncludes':
      passed = lines.some((l) => l.level !== 'error' && norm(l.text, caseSensitive).includes(norm(s.text, caseSensitive)));
      break;

    case 'noErrors':
      passed = !lines.some((l) => l.level === 'error');
      break;

    default:
      passed = false;
  }

  return { testId: test.id, passed, message: passed ? null : test.failMessage };
}

interface HarnessConfig {
  runId: string;
  tests: TestSpec[];
  settleMs: number;
}

/**
 * First script in the composed page. Captures console output and errors, waits for the page to
 * load and settle, runs the tests and posts the results to the app.
 */
export function harnessMain(cfg: HarnessConfig, evaluate: typeof evaluateTest): void {
  const lines: ConsoleLine[] = [];
  const post = (msg: Record<string, unknown>) => window.parent.postMessage({ ...msg, runId: cfg.runId }, '*');
  const format = (v: unknown) => {
    if (typeof v === 'string') return v;
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

  (['log', 'info', 'warn', 'error'] as const).forEach((level) => {
    const original = console[level].bind(console);
    console[level] = (...args: unknown[]) => {
      if (!fromExtension(new Error().stack)) record(level, args.map(format).join(' '));
      original(...args);
    };
  });

  window.addEventListener('error', (e) => {
    if (fromExtension(e.filename) || fromExtension(e.error?.stack)) return;
    record('error', e.message + (e.lineno ? ` (line ${e.lineno})` : ''));
  });

  // Keep the preview on the learner's page: links to other pages and form submissions don't leave
  // the sandbox (it couldn't load them anyway); the console explains what would have happened.
  document.addEventListener(
    'click',
    (e) => {
      const target = e.target as Element | null;
      const link = target?.closest?.('a[href]');
      const href = link?.getAttribute('href') ?? '';
      if (link && !href.startsWith('#')) {
        e.preventDefault();
        record('info', `Link to ${href} (links to other pages don't open in the preview, but they will on a real website)`);
        return;
      }
      const submit = target?.closest?.('button:not([type]), button[type="submit"], input[type="submit"]');
      if (submit?.closest('form')) {
        e.preventDefault();
        record('info', 'Form submitted! (In the preview, forms don\'t send anything anywhere.)');
      }
    },
    true,
  );
  document.addEventListener('submit', (e) => e.preventDefault(), true);
  window.addEventListener('unhandledrejection', (e) => {
    if (fromExtension(e.reason?.stack)) return;
    record('error', `Promise error: ${format(e.reason)}`);
  });

  const runTests = () => {
    const results = cfg.tests.map((t) => {
      try {
        return evaluate(t, document, window, lines);
      } catch {
        return { testId: t.id, passed: false, message: t.failMessage };
      }
    });
    post({ type: 'cg:done', results });
  };

  // Pages that fetch from the practice API (mockApi.ts) are tested once their requests have
  // answered, plus one more tick so the learner's .then() callbacks have run. Capped at 2 s.
  const startedAt = Date.now();
  const whenIdle = () => {
    const pending = (window as Window & { __cgMockPending?: number }).__cgMockPending ?? 0;
    if (pending > 0 && Date.now() - startedAt < 2000) setTimeout(whenIdle, 20);
    else setTimeout(runTests, 0);
  };

  window.addEventListener('load', () => setTimeout(whenIdle, cfg.settleMs));
}
