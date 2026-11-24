/**
 * Download project (exportProject.ts): the versions stay in step with Codegrove's own, the zip opens with
 * standard tools, and the export of every published FullStack solution builds as a real project:
 * `dotnet build` for the API, and `vite build` for the page (with Codegrove's own node_modules linked in,
 * so no install is needed). The build checks need the .NET SDK and run when it's there; CI has it.
 */
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readdirSync, readFileSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import { crc32, EXPORT_VERSIONS, projectFiles, projectSlug, zipStore } from './exportProject';

const web = process.cwd();
const repo = resolve(web, '..');
const hasDotnet = spawnSync('dotnet', ['--version']).status === 0;
const hasPython = spawnSync('python3', ['--version']).status === 0;

const solutions = readdirSync(`${repo}/src/Codegrove.Api/Content/courses/`)
  .filter((f) => f.endsWith('.json'))
  .flatMap((file) => {
    const course = JSON.parse(readFileSync(`${repo}/src/Codegrove.Api/Content/courses/${file}`, 'utf8')) as {
      lessons: { slug: string; title: string; isPublished: boolean; challenge?: { kind: string; solutionFiles: Record<string, string> } }[];
    };
    return course.lessons
      .filter((l) => l.isPublished && l.challenge?.kind === 'FullStack')
      .map((l) => ({ slug: l.slug, title: l.title, files: l.challenge!.solutionFiles }));
  });

describe('Download project', () => {
  it('uses the same versions as Codegrove', () => {
    const pkg = JSON.parse(readFileSync(`${web}/package.json`, 'utf8')) as { dependencies: Record<string, string>; devDependencies: Record<string, string> };
    const deps = { ...pkg.dependencies, ...pkg.devDependencies };
    expect(EXPORT_VERSIONS.react).toBe(deps.react);
    expect(EXPORT_VERSIONS.vite).toBe(deps.vite);
    expect(EXPORT_VERSIONS.pluginReact).toBe(deps['@vitejs/plugin-react']);
    const csproj = readFileSync(`${repo}/src/Codegrove.CSharpRunner/Codegrove.CSharpRunner.csproj`, 'utf8');
    expect(csproj).toContain(`Include="Microsoft.EntityFrameworkCore.InMemory" Version="${EXPORT_VERSIONS.efCoreInMemory}"`);
  });

  it('keeps the learner files unchanged and adds the project around them', () => {
    const files = projectFiles({ 'api/Program.cs': 'x', 'web/App.jsx': 'y', 'web/index.html': 'z', 'api/.setup.sql': 'hidden', 'notes.txt': 'n' }, 'The standings page');
    expect(files['the-standings-page/api/Program.cs']).toBe('x');
    expect(files['the-standings-page/web/App.jsx']).toBe('y');
    expect(Object.keys(files).sort()).toEqual([
      'the-standings-page/.gitignore',
      'the-standings-page/README.md',
      'the-standings-page/api/App.Api.csproj',
      'the-standings-page/api/Program.cs',
      'the-standings-page/api/Properties/launchSettings.json',
      'the-standings-page/web/App.jsx',
      'the-standings-page/web/index.html',
      'the-standings-page/web/package.json',
      'the-standings-page/web/vite.config.js',
    ]);
    expect(projectSlug('API: record results')).toBe('api-record-results');
    expect(projectSlug('!!!')).toBe('my-app');
  });

  it('computes CRC-32 like everyone else', () => {
    expect(crc32(new TextEncoder().encode('The quick brown fox jumps over the lazy dog'))).toBe(0x414fa339);
  });

  it.skipIf(!hasPython)('makes a zip that standard tools open', () => {
    const dir = mkdtempSync(join(tmpdir(), 'cg-zip-'));
    const files = { 'a/hello.txt': 'Hello, Grove!\n', 'a/b/ünïcode.md': 'Café ☕\n' };
    writeFileSync(`${dir}/t.zip`, zipStore(files));
    const listed = execFileSync('python3', ['-c',
      'import json,sys,zipfile; z=zipfile.ZipFile(sys.argv[1]); assert z.testzip() is None; print(json.dumps({n: z.read(n).decode() for n in z.namelist()}))',
      `${dir}/t.zip`]).toString();
    expect(JSON.parse(listed)).toEqual(files);
  });

  describe.skipIf(!hasDotnet)('every published FullStack solution builds as a real project', () => {
    it.each(solutions)('$slug', ({ title, files }) => {
      const dir = mkdtempSync(join(tmpdir(), 'cg-export-'));
      for (const [path, text] of Object.entries(projectFiles(files, title))) {
        mkdirSync(dirname(join(dir, path)), { recursive: true });
        writeFileSync(join(dir, path), text);
      }
      const root = join(dir, projectSlug(title));

      const api = spawnSync('dotnet', ['build', '-nologo', '-v', 'q', '-warnaserror'], { cwd: `${root}/api`, encoding: 'utf8' });
      expect(api.status, api.stdout + api.stderr).toBe(0);

      symlinkSync(`${web}/node_modules`, `${root}/web/node_modules`, 'dir');
      const page = spawnSync(process.execPath, [`${web}/node_modules/vite/bin/vite.js`, 'build', '--logLevel', 'warn'], { cwd: `${root}/web`, encoding: 'utf8' });
      expect(page.status, page.stdout + page.stderr).toBe(0);
    }, 240_000);
  });
});
