import { describe, expect, it } from 'vitest';
import { buildRunDocument, composeFiles, injectIntoHead } from './compose';
import { protectLoops } from './loopProtect';
import { evaluateTest } from './testEngine';
import type { ConsoleLine, TestKind, TestSpec } from './types';

const spec = (kind: TestKind, s: Record<string, unknown>): TestSpec => ({ id: 1, label: 'l', kind, spec: s, failMessage: 'nope' });

function page(html: string): Document {
  return new DOMParser().parseFromString(html, 'text/html');
}

describe('evaluateTest', () => {
  const doc = page(`<!DOCTYPE html><html><head><title>Ocean Facts</title></head>
    <body><main><h1>  Welcome   to my page </h1><p>Octopuses have three hearts.</p></main>
    <img src="a.png" alt="An otter"></body></html>`);
  const run = (t: TestSpec, lines: ConsoleLine[] = []) => evaluateTest(t, doc, window, lines).passed;

  it('checks existence and nesting with CSS selectors', () => {
    expect(run(spec('elementExists', { selector: 'main > h1 ~ p' }))).toBe(true);
    expect(run(spec('elementExists', { selector: 'footer' }))).toBe(false);
  });

  it('counts elements within bounds', () => {
    expect(run(spec('elementCount', { selector: 'h1', min: 1, max: 1 }))).toBe(true);
    expect(run(spec('elementCount', { selector: 'p', min: 2 }))).toBe(false);
  });

  it('compares text ignoring case and extra whitespace', () => {
    expect(run(spec('elementText', { selector: 'h1', contains: 'welcome to MY page' }))).toBe(true);
    expect(run(spec('elementText', { selector: 'title', notEquals: 'My first page', minLength: 3 }))).toBe(true);
    expect(run(spec('elementText', { selector: 'p', minLength: 100 }))).toBe(false);
  });

  it('checks attributes', () => {
    expect(run(spec('attribute', { selector: 'img', name: 'alt', notEmpty: true }))).toBe(true);
    expect(run(spec('attribute', { selector: 'img', name: 'title' }))).toBe(false);
  });

  it('checks console output and errors', () => {
    const lines: ConsoleLine[] = [{ level: 'log', text: 'Hello, Otter!' }];
    expect(run(spec('consoleIncludes', { text: 'hello, otter' }), lines)).toBe(true);
    expect(run(spec('noErrors', {}), lines)).toBe(true);
    expect(run(spec('noErrors', {}), [...lines, { level: 'error', text: 'x is not defined' }])).toBe(false);
  });

  it('returns the author\'s fail message on failure', () => {
    expect(evaluateTest(spec('elementExists', { selector: 'nav' }), doc, window, []).message).toBe('nope');
  });
});

describe('protectLoops', () => {
  it('leaves loop results unchanged for loops that finish', () => {
    const code = protectLoops('let t = 0; for (let i = 0; i < 5; i++) t += i; outer: while (true) { break outer; } return t;');
    expect(new Function(code)()).toBe(10);
  });

  it('stops infinite loops with a friendly RangeError', () => {
    const code = protectLoops('while (true) {}', 50);
    expect(() => new Function(code)()).toThrow(RangeError);
  });

  it('handles single-statement bodies and nesting', () => {
    const code = protectLoops('let n = 0; if (true) for (let i = 0; i < 3; i++) for (let j = 0; j < 3; j++) n++; return n;');
    expect(new Function(code)()).toBe(9);
  });

  it('returns code with syntax errors unchanged', () => {
    expect(protectLoops('while (')).toBe('while (');
  });
});

describe('composeFiles', () => {
  it('inlines linked CSS and JS and protects inline scripts', () => {
    const html = composeFiles({
      'index.html': '<head><link rel="stylesheet" href="style.css"></head><body><script src="script.js"></script><script>while(true){}</script></body>',
      'style.css': 'h1 { color: purple; }',
      'script.js': 'console.log("</script>")',
    });
    expect(html).toContain('<style>\nh1 { color: purple; }\n</style>');
    expect(html).toContain('<\\/script>');
    expect(html).toMatch(/__cg\d+=Date\.now\(\)/);
    expect(html).not.toContain('src="script.js"');
  });

  it('leaves links to files that do not exist alone', () => {
    const html = composeFiles({ 'index.html': '<link rel="stylesheet" href="missing.css">' });
    expect(html).toContain('href="missing.css"');
  });
});

describe('buildRunDocument', () => {
  it('puts the CSP and harness first inside <head>', () => {
    const out = buildRunDocument('<!DOCTYPE html><html><head><title>x</title></head><body></body></html>', 'r1', []);
    expect(out.indexOf('Content-Security-Policy')).toBeLessThan(out.indexOf('<title>'));
    expect(out).toContain("connect-src 'none'");
  });

  it('creates a <head> when the learner page has none', () => {
    expect(injectIntoHead('<h1>Hi</h1>', '<x>')).toBe('<!DOCTYPE html><head><x></head><h1>Hi</h1>');
  });

  it('cannot be broken out of with a test spec containing </script>', () => {
    const out = buildRunDocument('<p></p>', 'r1', [spec('elementExists', { selector: '</script><script>alert(1)' })]);
    expect(out.match(/<\/script>/g)?.length).toBe(1);
  });
});
