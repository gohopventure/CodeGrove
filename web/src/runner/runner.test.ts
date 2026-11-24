import { describe, expect, it } from 'vitest';
import { buildRunDocument, composeFiles, injectIntoHead } from './compose';
import { protectLoops } from './loopProtect';
import { runInJsdom } from './runInJsdom';
import { needsReact } from './react';
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

describe('harness with learner JavaScript', () => {
  const html = (body: string) => ({
    'index.html': `<!DOCTYPE html><html><head><title>t</title></head><body>${body}<script src="script.js"></script></body></html>`,
  });

  it('lets jsExpression tests read top-level let/const and drive event handlers', async () => {
    const files = {
      ...html('<button id="b">+1</button><p id="n">0</p>'),
      'script.js': `let clicks = 0;
        document.querySelector('#b').addEventListener('click', () => { clicks++; document.querySelector('#n').textContent = clicks; });`,
    };
    const { results } = await runInJsdom(files, [
      { ...spec('jsExpression', { expression: 'clicks', equals: 0 }), id: 1 },
      { ...spec('jsExpression', { expression: "(document.querySelector('#b').click(), document.querySelector('#b').click(), document.querySelector('#n').textContent)", equals: '2' }), id: 2 },
    ]);
    expect(results.map((r) => r.passed)).toEqual([true, true]);
  });

  it('reports runtime errors and console output', async () => {
    const files = { ...html(''), 'script.js': "console.log('Score:', 10);\nmissingFunction();" };
    const { results, console: lines } = await runInJsdom(files, [
      { ...spec('consoleIncludes', { text: 'Score: 10' }), id: 1 },
      { ...spec('noErrors', {}), id: 2 },
    ]);
    expect(results.map((r) => r.passed)).toEqual([true, false]);
    expect(lines.some((l) => l.level === 'error' && /missingFunction/.test(l.text))).toBe(true);
  });
});

describe('practice API (mockApi)', () => {
  const page = (script: string) => ({
    'index.html': '<!DOCTYPE html><html><head><title>t</title></head><body><ul id="list"></ul><script src="script.js"></script></body></html>',
    'script.js': script,
  });

  it('serves JSON, and tests wait until the fetched data is on the page', async () => {
    const { results } = await runInJsdom(
      page(`fetch('https://api.codegrove.test/planets')
        .then((response) => response.json())
        .then((planets) => {
          for (const p of planets) document.querySelector('#list').innerHTML += '<li>' + p.name + '</li>';
        });`),
      [{ ...spec('elementCount', { selector: '#list li', min: 8, max: 8 }), id: 1 }],
    );
    expect(results[0]?.passed).toBe(true);
  });

  it('works with async/await, returns 404s for unknown ids and rejects other hosts', async () => {
    const { results } = await runInJsdom(
      page(`async function go() {
          const ok = await fetch('https://api.codegrove.test/planets/3');
          const earth = await ok.json();
          const missing = await fetch('https://api.codegrove.test/planets/99');
          let offline = 'no';
          try { await fetch('https://example.com/data'); } catch (e) { offline = e.message; }
          window.out = [ok.status, earth.name, missing.ok, missing.status, offline];
        }
        go();`),
      [{ ...spec('jsExpression', { expression: 'window.out', equals: [200, 'Earth', false, 404, 'Failed to fetch'] }), id: 1 }],
    );
    expect(results[0]?.passed).toBe(true);
  });
});

describe('harness forms', () => {
  const files = (script: string) => ({
    'index.html': `<!DOCTYPE html><html><head><title>t</title></head><body>
      <form id="f"><input id="name" value="Rio"><button>Add</button></form><p id="out"></p>
      <script src="script.js"></script></body></html>`,
    'script.js': script,
  });
  const clickAdd = "(document.querySelector('#f button').click(), document.querySelector('#out').textContent)";

  it("runs the learner's submit handler when a submit button is clicked", async () => {
    const { results, console: lines } = await runInJsdom(
      files(`document.querySelector('#f').addEventListener('submit', (e) => {
        e.preventDefault();
        document.querySelector('#out').textContent = 'Hi ' + document.querySelector('#name').value + ' via ' + e.submitter.textContent;
      });`),
      [{ ...spec('jsExpression', { expression: clickAdd, equals: 'Hi Rio via Add' }), id: 1 }],
    );
    expect(results[0]?.passed).toBe(true);
    expect(lines.some((l) => l.text.startsWith('Form submitted!'))).toBe(false);
  });

  it('explains unhandled submissions in the console, and respects a canceled click', async () => {
    const { console: lines } = await runInJsdom(files(''), [{ ...spec('jsExpression', { expression: clickAdd, equals: '' }), id: 1 }]);
    expect(lines.filter((l) => l.text.startsWith('Form submitted!'))).toHaveLength(1);

    const { results } = await runInJsdom(
      files(`document.querySelector('#f button').addEventListener('click', (e) => e.preventDefault());
        document.querySelector('#f').addEventListener('submit', () => { document.querySelector('#out').textContent = 'submitted'; });`),
      [{ ...spec('jsExpression', { expression: clickAdd, equals: '' }), id: 1 }],
    );
    expect(results[0]?.passed).toBe(true);
  });
});

describe('React pages', () => {
  const html = '<!DOCTYPE html><html><head><title>t</title></head><body><div id="root"></div><script type="module" src="main.jsx"></script></body></html>';
  const main = 'import { createRoot } from "react-dom/client";\nimport App from "./App";\ncreateRoot(document.getElementById("root")).render(<App />);\n';
  const t = (id: number, kind: TestKind, s: Record<string, unknown>): TestSpec => ({ id, label: `t${id}`, kind, spec: s, failMessage: `fail ${id}` });

  it('renders components, re-renders after clicks and typing, and applies imported CSS', async () => {
    const files = {
      'App.jsx':
        'import { useState } from "react";\nimport "./App.css";\n' +
        'export default function App() {\n  const [count, setCount] = useState(0);\n  const [name, setName] = useState("");\n' +
        '  return (<main><h1>Hi {name || "friend"}</h1><input value={name} onChange={(e) => setName(e.target.value)} />' +
        '<button onClick={() => setCount(count + 1)}>Clicked {count}</button></main>);\n}\n',
      'App.css': 'h1 { color: rgb(0, 128, 128); }',
      'main.jsx': main,
      'index.html': html,
    };
    const { results, console } = await runInJsdom(files, [
      t(1, 'elementText', { selector: 'button', equals: 'Clicked 0' }),
      t(2, 'interact', { selector: 'button', click: true }),
      t(3, 'interact', { selector: 'button', click: true }),
      t(4, 'elementText', { selector: 'button', equals: 'Clicked 2' }),
      t(5, 'interact', { selector: 'input', type: 'Sam' }),
      t(6, 'elementText', { selector: 'h1', equals: 'Hi Sam' }),
      t(7, 'cssProperty', { selector: 'h1', property: 'color', equals: 'rgb(0, 128, 128)' }),
      t(8, 'jsExpression', { expression: '/useState/.test(window.__cgSources["App.jsx"])', equals: true }),
      t(9, 'noErrors', {}),
    ]);
    expect(console.filter((l) => l.level === 'error')).toEqual([]);
    expect(results.filter((r) => !r.passed).map((r) => r.message)).toEqual([]);
  });

  it('reports JSX syntax errors with the file and line', async () => {
    const files = { 'App.jsx': 'export default function App() {\n  return <h1>Oops;\n}\n', 'main.jsx': main, 'index.html': html };
    const { results, console } = await runInJsdom(files, [t(1, 'noErrors', {})]);
    expect(results[0]?.passed).toBe(false);
    expect(console.find((l) => l.level === 'error')?.text).toMatch(/^App\.jsx, line 2: Unterminated JSX contents\. .*closed/);
  });

  it('explains a missing file in an import', async () => {
    const files = { 'App.jsx': 'import Card from "./Crad";\nexport default function App() { return <Card />; }\n', 'main.jsx': main, 'index.html': html };
    const { console } = await runInJsdom(files, []);
    expect(console.map((l) => l.text).join('\n')).toMatch(/Can't find the file "\.\/Crad" \(imported in App\.jsx\)/);
  });

  it('runs an inline JSX script (lesson examples are a single file)', async () => {
    const page =
      '<!DOCTYPE html><html><head><title>t</title></head><body><div id="root"></div><script type="text/jsx">\n' +
      'import { createRoot } from "react-dom/client";\nconst items = ["a", "b"];\n' +
      'createRoot(document.getElementById("root")).render(<ul>{items.map((i) => <li key={i}>{i}</li>)}</ul>);\n</script></body></html>';
    const { results } = await runInJsdom({ 'index.html': page }, [t(1, 'elementCount', { selector: 'li', min: 2, max: 2 }), t(2, 'noErrors', {})]);
    expect(results.every((r) => r.passed)).toBe(true);
  });

  it('stops infinite loops in learner components', async () => {
    const files = {
      'App.jsx': 'export default function App() {\n  let n = 0;\n  while (true) { n++; }\n  return <p>{n}</p>;\n}\n',
      'main.jsx': main,
      'index.html': html,
    };
    const { console } = await runInJsdom(files, []);
    expect(console.map((l) => l.text).join('\n')).toMatch(/loop has been running for a long time/);
  });

  it('shows React warnings with their placeholders filled in', async () => {
    const files = {
      'App.jsx': 'const a = ["x", "y"];\nexport default function App() {\n  return <ul>{a.map((s) => <li>{s}</li>)}</ul>;\n}\n',
      'main.jsx': main,
      'index.html': html,
    };
    const { results, console } = await runInJsdom(files, [t(1, 'noErrors', {})]);
    expect(results[0]?.passed).toBe(false);
    const warning = console.find((l) => l.level === 'error')?.text ?? '';
    expect(warning).toMatch(/unique "key" prop/);
    expect(warning).not.toMatch(/%s/);
  });

  it('checks data fetched in useEffect, and fetches started by a click', async () => {
    const files = {
      'App.jsx':
        'import { useEffect, useState } from "react";\n' +
        'export default function App() {\n' +
        '  const [pets, setPets] = useState([]);\n  const [planet, setPlanet] = useState(null);\n' +
        '  useEffect(() => {\n    fetch("https://api.codegrove.test/pets").then((r) => r.json()).then(setPets);\n  }, []);\n' +
        '  const load = () => fetch("https://api.codegrove.test/planets/6").then((r) => r.json()).then(setPlanet);\n' +
        '  return (<main><ul>{pets.map((p) => <li key={p.id}>{p.name}</li>)}</ul>' +
        '<button onClick={load}>Load</button><p id="planet">{planet ? planet.name : "none"}</p></main>);\n}\n',
      'main.jsx': main,
      'index.html': html,
    };
    const { results } = await runInJsdom(files, [
      t(1, 'elementCount', { selector: 'li', min: 5, max: 5 }),
      t(2, 'interact', { selector: 'button', click: true }),
      t(3, 'elementText', { selector: '#planet', equals: 'Saturn' }),
    ]);
    expect(results.filter((r) => !r.passed).map((r) => r.message)).toEqual([]);
  });

  it('submits forms to React onSubmit handlers', async () => {
    const files = {
      'App.jsx':
        'import { useState } from "react";\n' +
        'export default function App() {\n  const [text, setText] = useState("");\n  const [items, setItems] = useState([]);\n' +
        '  function add(e) {\n    e.preventDefault();\n    setItems([...items, text]);\n    setText("");\n  }\n' +
        '  return (<form onSubmit={add}><input value={text} onChange={(e) => setText(e.target.value)} />' +
        '<button>Add</button><ul>{items.map((i) => <li key={i}>{i}</li>)}</ul></form>);\n}\n',
      'main.jsx': main,
      'index.html': html,
    };
    const { results, console } = await runInJsdom(files, [
      t(1, 'interact', { selector: 'input', type: 'kite' }),
      t(2, 'interact', { selector: 'button', click: true }),
      t(3, 'elementText', { selector: 'li', equals: 'kite' }),
      t(4, 'jsExpression', { expression: 'document.querySelector("input").value', equals: '' }),
      t(5, 'noErrors', {}),
    ]);
    expect(console.map((l) => l.text).join('\n')).not.toMatch(/Form submitted/);
    expect(results.filter((r) => !r.passed).map((r) => r.message)).toEqual([]);
  });

  it('leaves plain pages alone', () => {
    expect(needsReact({ 'index.html': '<script>let a = 1;</script>', 'script.js': '' })).toBe(false);
    expect(needsReact({ 'index.html': '', 'App.jsx': '' })).toBe(true);
  });
});

describe('live API bridge (capstone)', () => {
  const page = (script: string) => ({
    'index.html': '<!DOCTYPE html><html><head><title>t</title></head><body><ul id="list"></ul><script src="script.js"></script></body></html>',
    'script.js': script,
  });

  it("sends relative and localhost URLs to the learner's server, with method, headers and body", async () => {
    const seen: string[] = [];
    const { results } = await runInJsdom(
      page(`async function go() {
          const made = await fetch('/api/clubs', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ name: 'Owl Watchers' }),
          });
          const list = await (await fetch('http://localhost:5080/api/clubs?sort=name')).json();
          for (const c of list) document.querySelector('#list').innerHTML += '<li>' + c.name + '</li>';
          const planets = await fetch('https://api.codegrove.test/planets');
          window.out = [made.status, made.headers.get('location'), planets.status];
        }
        go();`),
      [
        { ...spec('elementText', { selector: '#list li', text: 'Owl Watchers' }), id: 1 },
        { ...spec('jsExpression', { expression: 'window.out', equals: [201, '/api/clubs/1', 200] }), id: 2 },
      ],
      async (r): Promise<{ status: number; statusText: string; headers: Record<string, string>; body: string }> => {
        seen.push(`${r.method} ${r.path} ${r.headers['content-type'] ?? ''} ${r.body ?? ''}`.trim());
        return r.method === 'POST'
          ? { status: 201, statusText: 'Created', headers: { 'Content-Type': 'application/json', Location: '/api/clubs/1' }, body: '{"id":1,"name":"Owl Watchers"}' }
          : { status: 200, statusText: 'OK', headers: { 'Content-Type': 'application/json' }, body: '[{"id":1,"name":"Owl Watchers"}]' };
      },
    );
    expect(results.map((r) => r.passed)).toEqual([true, true]);
    expect(seen).toEqual(['POST /api/clubs application/json {"name":"Owl Watchers"}', 'GET /api/clubs?sort=name']);
  });

  it("rejects like a real network failure, and logs why, when the server can't answer", async () => {
    const { results, console: lines } = await runInJsdom(
      page(`fetch('/api/clubs').then(() => { window.out = 'answered'; }, (e) => { window.out = e.message; });`),
      [{ ...spec('jsExpression', { expression: 'window.out', equals: 'Failed to fetch' }), id: 1 }],
      async () => ({ error: "Your API isn't running. Press Run to start it." }),
    );
    expect(results[0]?.passed).toBe(true);
    expect(lines).toContainEqual({ level: 'error', text: "Your API isn't running. Press Run to start it." });
  });

  it('leaves relative URLs alone on pages without a live API', async () => {
    const { results } = await runInJsdom(
      page(`fetch('/api/clubs').then((r) => { window.out = r.status; });`),
      [{ ...spec('jsExpression', { expression: 'window.out', equals: 404 }), id: 1 }],
    );
    expect(results[0]?.passed).toBe(true);
  });
});
