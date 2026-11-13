/**
 * Test-only type checker: the `typescript` package, with learner files kept in memory and the
 * standard library read from node_modules. Never bundled into the app (the app uses Monaco's worker,
 * see lesson/typeCheckMonaco.ts). Same compiler options and the same TypeScript version (5.9).
 */
import ts from 'typescript';
import { needsAngular } from './angular';
import type { Files } from './compose';
import { flattenMessage, sortDiagnostics, TS_COMPILER_OPTIONS, typeScriptFiles, type TypeChecker, type TypeDiagnostic } from './typeCheck';

const ROOT = '/learner/';
const options = TS_COMPILER_OPTIONS as unknown as ts.CompilerOptions;

// Library files never change, so parse them once for the whole test run (they're large).
const libCache = new Map<string, ts.SourceFile | undefined>();

/**
 * Angular's .d.ts files (vite-plugins/angularRuntime.ts), keyed "/node_modules/@angular/core/…". The
 * learner folder is /learner/, so module resolution looks in /node_modules/ and finds them, exactly as
 * Monaco does with the same files at file:///node_modules/….
 */
let angularTypes: Promise<Map<string, string>> | null = null;
const loadAngularTypes = () => (angularTypes ??= import('virtual:cg-angular-types').then((m) => new Map(Object.entries(m.default))));

export const nodeTypeChecker: TypeChecker = async (files: Files) => {
  const names = typeScriptFiles(files);
  if (names.length === 0) return [];

  const learner = new Map(names.map((n) => [ROOT + n, files[n] ?? '']));
  const packages = needsAngular(files) ? await loadAngularTypes() : new Map<string, string>();
  const inPackages = (d: string) => {
    const dir = d.endsWith('/') ? d : `${d}/`;
    for (const key of packages.keys()) if (key.startsWith(dir)) return true;
    return false;
  };
  const base = ts.createCompilerHost(options, true);
  const host: ts.CompilerHost = {
    ...base,
    fileExists: (f) => learner.has(f) || packages.has(f) || (!f.startsWith(ROOT) && !f.startsWith('/node_modules/') && base.fileExists(f)),
    readFile: (f) => learner.get(f) ?? packages.get(f) ?? (f.startsWith(ROOT) || f.startsWith('/node_modules/') ? undefined : base.readFile(f)),
    // The learner folder (and the packages) only exist in memory; module resolution asks whether they're there.
    directoryExists: (d) =>
      ROOT.startsWith(d.endsWith('/') ? d : `${d}/`) || inPackages(d) || (!d.startsWith(ROOT) && !d.startsWith('/node_modules') && (base.directoryExists?.(d) ?? false)),
    realpath: (p) => p,
    getCurrentDirectory: () => ROOT,
    getSourceFile: (fileName, languageVersion, onError) => {
      const text = learner.get(fileName);
      if (text !== undefined) return ts.createSourceFile(fileName, text, languageVersion, true);
      const declarations = packages.get(fileName);
      if (declarations !== undefined) {
        if (!libCache.has(fileName)) libCache.set(fileName, ts.createSourceFile(fileName, declarations, languageVersion, true));
        return libCache.get(fileName);
      }
      if (!libCache.has(fileName)) libCache.set(fileName, base.getSourceFile(fileName, languageVersion, onError));
      return libCache.get(fileName);
    },
  };

  const program = ts.createProgram([...learner.keys()], options, host);
  const found: TypeDiagnostic[] = [];
  for (const source of program.getSourceFiles().filter((s) => learner.has(s.fileName))) {
    for (const d of [...program.getSyntacticDiagnostics(source), ...program.getSemanticDiagnostics(source)]) {
      if (d.category !== ts.DiagnosticCategory.Error) continue;
      const { line, character } = source.getLineAndCharacterOfPosition(d.start ?? 0);
      found.push({
        file: source.fileName.slice(ROOT.length),
        line: line + 1,
        column: character + 1,
        code: d.code,
        message: flattenMessage(d.messageText),
      });
    }
  }
  // Options-level problems (none expected) would point at no file; surfacing them keeps them visible.
  for (const d of program.getOptionsDiagnostics()) {
    found.push({ file: names[0]!, line: 1, column: 1, code: d.code, message: `Compiler setup: ${flattenMessage(d.messageText)}` });
  }
  return sortDiagnostics(files, found);
};
