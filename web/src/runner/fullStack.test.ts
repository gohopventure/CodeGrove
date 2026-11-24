import { describe, expect, it } from 'vitest';
import { apiFiles, mergeResults, parseExampleFiles, splitTests, webFiles } from './fullStack';
import type { TestSpec } from './types';

const t = (id: number, kind: TestSpec['kind'], spec: Record<string, unknown> = {}): TestSpec => ({ id, label: `t${id}`, kind, spec, failMessage: 'f.' });

describe('fullStack helpers', () => {
  it('sends C# kinds to the API, page kinds to the preview, and shared kinds by side', () => {
    const tests = [
      t(1, 'httpRequest', { path: '/api/clubs' }), t(2, 'elementText', { selector: 'li' }), t(3, 'noErrors', { side: 'api' }),
      t(4, 'noErrors', { side: 'web' }), t(5, 'sourceMatches', { file: 'api/Program.cs', pattern: 'x' }), t(6, 'interact', {}),
      t(7, 'consoleIncludes', { side: 'api', text: 'x' }),
    ];
    const { api, web } = splitTests(tests);
    expect(api.map((x) => x.id)).toEqual([1, 3, 5, 7]);
    expect(web.map((x) => x.id)).toEqual([2, 4, 6]);
  });

  it('keeps api/ names and strips the web/ prefix', () => {
    const files = { 'api/Program.cs': 'a', 'web/App.jsx': 'b', 'web/index.html': 'c' };
    expect(apiFiles(files)).toEqual({ 'api/Program.cs': 'a' });
    expect(webFiles(files)).toEqual({ 'App.jsx': 'b', 'index.html': 'c' });
  });

  it('merges results in test order, or null when a side has none', () => {
    const tests = [t(1, 'httpRequest'), t(2, 'elementText')];
    const api = [{ testId: 1, passed: true, message: null }];
    const web = [{ testId: 2, passed: false, message: 'no' }];
    expect(mergeResults(tests, api, web)?.map((r) => r.testId)).toEqual([1, 2]);
    expect(mergeResults(tests, null, web)).toBeNull();
    expect(mergeResults(tests, api, [])).toBeNull();
  });

  it('reads an example made of files', () => {
    expect(parseExampleFiles('{"api/Program.cs":"x","web/index.html":"y"}')).toEqual({ 'api/Program.cs': 'x', 'web/index.html': 'y' });
    expect(parseExampleFiles('Console.WriteLine(1);')).toBeNull();
    expect(parseExampleFiles('{"a":1}')).toBeNull();
  });
});
