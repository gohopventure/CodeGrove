/*
 * Loads Monaco from our own bundle instead of the default CDN. That keeps children's browsers from
 * contacting a third-party host (privacy) and lets the app CSP stay at script-src 'self'.
 */
import * as monaco from 'monaco-editor';
import { loader } from '@monaco-editor/react';
import EditorWorker from 'monaco-editor/editor/editor.worker?worker';
import HtmlWorker from 'monaco-editor/language/html/html.worker?worker';
import CssWorker from 'monaco-editor/language/css/css.worker?worker';
import TsWorker from 'monaco-editor/language/typescript/ts.worker?worker';
import JsonWorker from 'monaco-editor/language/json/json.worker?worker';

self.MonacoEnvironment = {
  getWorker(_workerId: string, label: string) {
    if (label === 'html') return new HtmlWorker();
    if (label === 'css') return new CssWorker();
    if (label === 'json') return new JsonWorker();
    if (label === 'typescript' || label === 'javascript') return new TsWorker();
    return new EditorWorker();
  },
};

// React lessons edit .jsx files. The TypeScript worker parses JSX in .jsx files; telling it JSX is
// expected keeps it from flagging every tag. Imports like "react" can't be resolved in the editor,
// which is fine: semantic checks stay off for JavaScript, only syntax errors are underlined.
monaco.typescript.javascriptDefaults.setCompilerOptions({
  ...monaco.typescript.javascriptDefaults.getCompilerOptions(),
  jsx: monaco.typescript.JsxEmit.Preserve,
});

loader.config({ monaco });

export { monaco };
