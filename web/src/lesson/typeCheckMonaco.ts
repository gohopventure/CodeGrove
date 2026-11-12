/**
 * The app's type checker: Monaco's own TypeScript worker. It's the same TypeScript (5.9) that draws
 * the editor's red squiggles, it's already loaded on the lesson page, and it runs in a web worker, so
 * checking adds no download and never blocks typing. Tests see exactly what the editor underlines.
 *
 * Every model is keyed by the file name the editor uses (@monaco-editor/react calls Uri.parse(path)),
 * so the checker and the editor share models: checking a file the learner hasn't opened yet simply
 * creates its model early, and imports between lesson files resolve in the editor too.
 */
import type { Files } from '../api/types';
import { flattenMessage, sortDiagnostics, TS_COMPILER_OPTIONS, typeScriptFiles, type TypeChecker, type TypeDiagnostic } from '../runner/typeCheck';
import { monaco } from './monacoSetup';

// Learner TypeScript is checked strictly, with the same options as the content tests.
monaco.typescript.typescriptDefaults.setCompilerOptions(TS_COMPILER_OPTIONS as unknown as monaco.typescript.CompilerOptions);
// Keep every TypeScript model in the worker (not just open ones), so imports between files resolve.
monaco.typescript.typescriptDefaults.setEagerModelSync(true);

const uriFor = (name: string) => monaco.Uri.parse(name);

/**
 * Makes the worker's set of TypeScript files match this lesson: creates or updates a model per .ts
 * file and disposes TypeScript models left over from other lessons (their code would otherwise still
 * count, e.g. an old types.ts declaring the same names). Called by the editor and the checker.
 */
export function syncTypeScriptModels(files: Files): void {
  const names = typeScriptFiles(files);
  const keep = new Set(names.map((n) => uriFor(n).toString()));

  for (const model of monaco.editor.getModels()) {
    if (model.getLanguageId() === 'typescript' && !keep.has(model.uri.toString()) && !model.isAttachedToEditor()) model.dispose();
  }
  for (const name of names) {
    const uri = uriFor(name);
    const text = files[name] ?? '';
    const model = monaco.editor.getModel(uri);
    if (!model) monaco.editor.createModel(text, 'typescript', uri);
    // A model open in the editor holds what the learner is typing right now, which can be newer than
    // `files` (the preview runs 400 ms after typing stops). Never overwrite it.
    else if (model.getValue() !== text && !model.isAttachedToEditor()) model.setValue(text);
  }
}

export const monacoTypeChecker: TypeChecker = async (files) => {
  const names = typeScriptFiles(files);
  if (names.length === 0) return [];
  syncTypeScriptModels(files);

  const uris = names.map(uriFor);
  const getWorker = await monaco.typescript.getTypeScriptWorker();
  const worker = await getWorker(...uris);

  const found: TypeDiagnostic[] = [];
  for (const [i, uri] of uris.entries()) {
    const model = monaco.editor.getModel(uri);
    if (!model) continue;
    const key = uri.toString();
    const [syntax, semantic] = await Promise.all([worker.getSyntacticDiagnostics(key), worker.getSemanticDiagnostics(key)]);
    for (const d of [...syntax, ...semantic]) {
      if (d.category !== 1) continue; // errors only; suggestions and warnings aren't failures
      const pos = model.getPositionAt(d.start ?? 0);
      found.push({ file: names[i]!, line: pos.lineNumber, column: pos.column, code: d.code, message: flattenMessage(d.messageText) });
    }
  }
  return sortDiagnostics(files, found);
};
