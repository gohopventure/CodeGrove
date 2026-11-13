/**
 * Angular pages: decorators are lowered to plain calls (angular.ts), templates are compiled in the
 * frame by Angular's JIT compiler (vite-plugins/angularRuntime.ts), and the harness waits for Angular
 * to be stable before each test. Type checking uses Angular's real .d.ts files.
 */
import { describe, expect, it } from 'vitest';
import { DecoratorError, lowerDecorators, needsAngular } from './angular';
import { runInJsdom } from './runInJsdom';
import { nodeTypeChecker } from './typeCheckNode';
import type { TestKind, TestSpec } from './types';

const tests = (...list: [TestKind, Record<string, unknown>][]): TestSpec[] =>
  list.map(([kind, spec], i) => ({ id: i + 1, label: `t${i + 1}`, kind, spec, failMessage: `fail ${i + 1}` }));

const INDEX = `<!DOCTYPE html><html><head><title>T</title></head><body><app-root></app-root><script type="module" src="main.ts"></script></body></html>`;
const MAIN = `import { bootstrapApplication } from '@angular/platform-browser';\nimport { App } from './app';\n\nbootstrapApplication(App).catch((err) => console.error(err));\n`;

const page = (app: string, extra: Record<string, string> = {}) => ({ 'app.ts': app, ...extra, 'main.ts': MAIN, 'index.html': INDEX });

const COUNTER = `import { Component, computed, signal } from '@angular/core';

@Component({
  selector: 'app-root',
  template: \`
    <h1>Hello, {{ name() }}!</h1>
    <button (click)="add()">Clicks: {{ count() }}</button>
    <p class="double">{{ double() }}</p>
    @if (count() >= 2) { <p class="win">You did it!</p> }
  \`,
})
export class App {
  name = signal('Juni');
  count = signal(0);
  double = computed(() => this.count() * 2);

  add() {
    this.count.update((n) => n + 1);
  }
}
`;

describe('needsAngular', () => {
  it('spots imports of Angular packages', () => {
    expect(needsAngular({ 'app.ts': "import { Component } from '@angular/core';" })).toBe(true);
    expect(needsAngular({ 'main.ts': 'import "@angular/compiler";' })).toBe(true);
    expect(needsAngular({ 'main.ts': 'const angular = "@angular/core";' })).toBe(false);
    expect(needsAngular({ 'App.jsx': "import { useState } from 'react';" })).toBe(false);
  });
});

describe('lowerDecorators', () => {
  it('leaves code without decorators alone', () => {
    const src = 'const email = "a@b.c";\nexport class A {}\n';
    expect(lowerDecorators(src, 'a.ts')).toBe(src);
  });

  it('moves a class decorator after the class, keeping every line number', () => {
    const out = lowerDecorators(COUNTER, 'app.ts');
    expect(out.split('\n')).toHaveLength(COUNTER.split('\n').length);
    expect(out).not.toMatch(/@Component/);
    const lines = out.split('\n');
    expect(lines.findIndex((l) => l.includes('export class App'))).toBe(COUNTER.split('\n').findIndex((l) => l.includes('export class App')));
    expect(lines.findIndex((l) => l.includes('this.count.update'))).toBe(COUNTER.split('\n').findIndex((l) => l.includes('this.count.update')));
    // The multi-line template is joined onto the closing-brace line with \n escapes inside the backticks.
    expect(out).toMatch(/^\} __cgDecorate\(App, \[Component\(\{ selector: 'app-root', template: `\\n {4}<h1>/m);
  });

  it('handles several decorators, several classes, comments and regexes', () => {
    const src = [
      "@Injectable({ providedIn: 'root' }) // shared",
      'export class Pets {',
      "  slug = (s: string) => s.replace(/[{}]/g, '');",
      '}',
      '',
      '@Pipe({ name: "shout" })',
      '@Other()',
      'export class Shout { transform(v: string) { return `${v}!`; } }',
    ].join('\n');
    const out = lowerDecorators(src, 'x.ts');
    expect(out).toContain("} __cgDecorate(Pets, [Injectable({ providedIn: 'root' })]);");
    expect(out).toContain('} } __cgDecorate(Shout, [Pipe({ name: "shout" }), Other()]);');
    expect(out.split('\n')).toHaveLength(src.split('\n').length);
  });

  it('records signal inputs, models and outputs for the JIT compiler', () => {
    const src = [
      "@Component({ selector: 'x', template: '' })",
      'export class Card {',
      '  name = input.required<string>();',
      "  size = input<'s' | 'm'>('s');",
      '  label: InputSignal<string> = input("");',
      '  open = model(false);',
      '  picked = output<string>();',
      '  count = signal(0);',
      '  same = this.count() == 1;',
      '  pick = (v: string) => this.picked.emit(v);',
      '  helper() { const inner = input(); return inner; }',
      '}',
    ].join('\n');
    const out = lowerDecorators(src, 'card.ts');
    expect(out).toContain(
      `[["name","input",true],["size","input",false],["label","input",false],["open","model",false],["picked","output",false]]);`,
    );
  });

  it('explains property decorators, with the line', () => {
    const src = "import { Component, Input } from '@angular/core';\n\n@Component({ selector: 'x', template: '' })\nexport class Card {\n  @Input() name = '';\n}\n";
    expect(() => lowerDecorators(src, 'card.ts')).toThrow(DecoratorError);
    expect(() => lowerDecorators(src, 'card.ts')).toThrow(/^card\.ts, line 5: @Input is a decorator inside a class.*Use input\(\) instead/);
  });

  it('explains a decorator that is not above a class', () => {
    expect(() => lowerDecorators("@Component({ selector: 'x' })\nconst x = 1;\n", 'a.ts')).toThrow(/^a\.ts, line 1: A decorator like @Component must sit right above a class/);
  });

  it('ignores @ inside strings and templates', () => {
    const src = "const t = `@Component() ${'@x'}`;\nconst s = '@Input()';\n";
    expect(lowerDecorators(src, 'a.ts')).toBe(src);
  });
});

describe('Angular pages in the preview', () => {
  it('renders a component, then re-renders after clicks', async () => {
    const run = await runInJsdom(
      page(COUNTER),
      tests(
        ['elementText', { selector: 'h1', equals: 'Hello, Juni!' }],
        ['elementCount', { selector: '.win', max: 0 }],
        ['interact', { selector: 'button', click: true }],
        ['interact', { selector: 'button', click: true }],
        ['elementText', { selector: 'button', equals: 'Clicks: 2' }],
        ['elementText', { selector: '.double', equals: '4' }],
        ['elementExists', { selector: '.win' }],
        ['noErrors', {}],
      ),
    );
    expect(run.results.map((r) => r.passed)).toEqual([true, true, true, true, true, true, true, true]);
  });

  it('runs services, inputs, outputs and @for across files', async () => {
    const pets = `import { Injectable, signal } from '@angular/core';

@Injectable({ providedIn: 'root' })
export class PetService {
  pets = signal(['Mochi', 'Pip']);
  add(name: string) {
    this.pets.update((list) => [...list, name]);
  }
}
`;
    const card = `import { Component, input, output } from '@angular/core';

@Component({
  selector: 'app-pet-card',
  template: \`<li (click)="picked.emit(name())">{{ name() }}</li>\`,
})
export class PetCard {
  name = input.required<string>();
  picked = output<string>();
}
`;
    const app = `import { Component, inject, signal } from '@angular/core';
import { PetCard } from './pet-card';
import { PetService } from './pet.service';

@Component({
  selector: 'app-root',
  imports: [PetCard],
  template: \`
    <ul>@for (pet of service.pets(); track pet) { <app-pet-card [name]="pet" (picked)="chosen.set($event)" /> }</ul>
    <button (click)="service.add('Biscuit')">Add</button>
    <p id="chosen">{{ chosen() }}</p>
  \`,
})
export class App {
  service = inject(PetService);
  chosen = signal('nobody');
}
`;
    const files = page(app, { 'pet.service.ts': pets, 'pet-card.ts': card });
    const run = await runInJsdom(
      files,
      tests(
        ['elementCount', { selector: 'li', min: 2, max: 2 }],
        ['interact', { selector: 'button', click: true }],
        ['elementCount', { selector: 'li', min: 3, max: 3 }],
        ['interact', { selector: 'li', click: true }],
        ['elementText', { selector: '#chosen', equals: 'Mochi' }],
        ['typeCheck', {}],
      ),
    );
    expect(run.results.map((r) => r.passed), JSON.stringify(run.console)).toEqual([true, true, true, true, true, true]);
  });

  it('loads templateUrl and styleUrl from the lesson files, like an Angular CLI project', async () => {
    const app = `import { Component, signal } from '@angular/core';

@Component({
  selector: 'app-root',
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App {
  title = signal('Quest Board');
}
`;
    const run = await runInJsdom(
      page(app, { 'app.html': '<h1 class="title">{{ title() }}</h1>\n', 'app.css': '.title { color: rgb(0, 128, 128); }\n' }),
      tests(['elementText', { selector: 'h1', equals: 'Quest Board' }], ['cssProperty', { selector: 'h1', property: 'color', equals: 'rgb(0, 128, 128)' }], ['noErrors', {}]),
    );
    expect(run.results.map((r) => r.passed), JSON.stringify(run.console)).toEqual([true, true, true]);
  });

  it('explains a templateUrl that points at a missing file', async () => {
    const app = "import { Component } from '@angular/core';\n\n@Component({ selector: 'app-root', templateUrl: './app.htm' })\nexport class App {}\n";
    const run = await runInJsdom(page(app), tests(['noErrors', {}]));
    expect(run.console.find((l) => l.level === 'error')?.text).toMatch(/Can't find the file "\.\/app\.htm"/);
  });

  it('runs a single-page example with inline TypeScript', async () => {
    const example = `<!DOCTYPE html><html><head><title>E</title></head><body><app-root></app-root>
<script type="text/typescript">
import { Component, signal } from '@angular/core';
import { bootstrapApplication } from '@angular/platform-browser';

@Component({ selector: 'app-root', template: '<p>{{ msg() }}</p>' })
class App {
  msg = signal('inline works');
}

bootstrapApplication(App);
</script></body></html>`;
    expect(needsAngular({ 'index.html': example })).toBe(true);
    const run = await runInJsdom({ 'index.html': example }, tests(['elementText', { selector: 'p', equals: 'inline works' }], ['noErrors', {}]));
    expect(run.results.map((r) => r.passed), JSON.stringify(run.console)).toEqual([true, true]);
  });

  it('types into an input bound with (input)', async () => {
    const app = `import { Component, signal } from '@angular/core';

@Component({
  selector: 'app-root',
  template: \`<input (input)="name.set($any($event.target).value)" /><p>Hi {{ name() }}</p>\`,
})
export class App {
  name = signal('');
}
`;
    const run = await runInJsdom(page(app), tests(['interact', { selector: 'input', type: 'Sam' }], ['elementText', { selector: 'p', equals: 'Hi Sam' }]));
    expect(run.results.every((r) => r.passed)).toBe(true);
  });

  it('reports template mistakes in the console', async () => {
    const app = COUNTER.replace('</button>', '</buton>');
    const run = await runInJsdom(page(app), tests(['noErrors', {}]));
    expect(run.results[0]!.passed).toBe(false);
    expect(run.console.find((l) => l.level === 'error')?.text, JSON.stringify(run.console)).toMatch(/buton/);
  });

  it('reports a property decorator as an error in the right file and line', async () => {
    const app = COUNTER.replace("name = signal('Juni');", "@Input() name = 'Juni';");
    const run = await runInJsdom(page(app), tests(['noErrors', {}]));
    expect(run.console.find((l) => l.level === 'error')?.text, JSON.stringify(run.console)).toMatch(/^app\.ts, line 13: @Input is a decorator inside a class/);
  });

  // jsdom ignores //# sourceURL, so the file and line themselves are checked in a real browser (Playwright).
  it('reports runtime errors in a click handler, never blaming Angular-generated code', async () => {
    const app = COUNTER.replace('this.count.update((n) => n + 1);', 'this.cont.update((n: number) => n + 1);');
    const run = await runInJsdom(page(app), tests(['interact', { selector: 'button', click: true }], ['noErrors', {}]));
    expect(run.results[1]!.passed).toBe(false);
    const error = run.console.find((l) => l.level === 'error')?.text ?? '';
    expect(error).toMatch(/TypeError: Cannot read properties of undefined \(reading 'update'\)/);
    expect(error).not.toMatch(/App\.js/);
  });
});

describe('Angular type checking', () => {
  it('knows Angular’s real types', async () => {
    expect(await nodeTypeChecker(page(COUNTER))).toEqual([]);
  });

  it('catches a wrong type in a signal', async () => {
    const [d] = await nodeTypeChecker(page(COUNTER.replace("this.count.update((n) => n + 1);", "this.count.set('lots');")));
    expect(d).toMatchObject({ file: 'app.ts', code: 2345 });
  });

  it('catches a missing required input type argument use', async () => {
    const card = "import { Component, input } from '@angular/core';\n@Component({ selector: 'c', template: '' })\nexport class C {\n  n = input.required<number>();\n  x: string = this.n();\n}\n";
    const [d] = await nodeTypeChecker({ 'c.ts': card });
    expect(d).toMatchObject({ file: 'c.ts', line: 5, code: 2322 });
  });
});
