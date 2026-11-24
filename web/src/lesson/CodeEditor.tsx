import Editor from '@monaco-editor/react';
import { useEffect, useState } from 'react';
import type { Files } from '../api/types';
import './monacoSetup';
import { syncTypeScriptModels } from './typeCheckMonaco';

const LANGUAGE: Record<string, string> = {
  html: 'html', css: 'css', js: 'javascript', jsx: 'javascript', ts: 'typescript', tsx: 'typescript', json: 'json', cs: 'csharp', sql: 'sql',
};
/** What a folder is called above its tabs (FullStack challenges keep the API in api/ and the page in web/). */
const FOLDER_LABELS: Record<string, string> = { api: 'API', web: 'Page', db: 'Database' };
const languageFor = (file: string) => LANGUAGE[file.split('.').pop() ?? ''] ?? 'plaintext';

/**
 * The only component that knows about Monaco. Swapping in CodeMirror 6 (better on tablets) later
 * means replacing this file; lessons and the player only see files in and files out.
 */
export function CodeEditor({
  files,
  onChange,
  dark,
  readOnly = false,
  label = 'Code editor',
  modelPrefix = '',
}: {
  files: Files;
  onChange?: (files: Files) => void;
  dark: boolean;
  readOnly?: boolean;
  label?: string;
  /** Prepended to each file's Monaco model name, so a second editor with the same file names (a FullStack example) keeps its own text and markers. */
  modelPrefix?: string;
}) {
  const names = Object.keys(files).filter((name) => !name.startsWith('.')); // dotfiles are app data, not learner code
  const [active, setActive] = useState(names[0] ?? 'index.html');
  const [fontSize, setFontSize] = useState(16);

  // TypeScript lessons: give every .ts file a model up front (not only the open tab), so an import
  // like `import { Pet } from "./types"` resolves and the squiggles are right before the tab is opened.
  // Re-run only when the set of files changes, not on every keystroke.
  const fileKey = names.join('|');
  useEffect(() => {
    if (!readOnly) syncTypeScriptModels(files);
  }, [fileKey, readOnly]); // keyed on file names on purpose, not on every keystroke

  return (
    <div className="flex h-full min-h-[320px] flex-col overflow-hidden rounded-card border border-line bg-surface">
      <div className="flex items-center gap-1 border-b border-line bg-surface-2 px-2 py-1">
        <div role="tablist" aria-label="Files" className="flex flex-wrap items-center gap-1">
          {names.map((name, i) => {
            // Files in folders (FullStack: api/…, web/…) show a short name, with the folder's label before
            // the first file of each folder.
            const slash = name.indexOf('/');
            const folder = slash > 0 ? name.slice(0, slash) : null;
            const firstInFolder = folder !== null && (i === 0 || !names[i - 1]!.startsWith(`${folder}/`));
            return (
              <span key={name} className="contents">
                {firstInFolder && (
                  <span aria-hidden="true" className={`px-1 text-xs font-bold uppercase tracking-wide text-muted ${i > 0 ? 'ml-2 border-l border-line pl-3' : ''}`}>
                    {FOLDER_LABELS[folder] ?? folder}
                  </span>
                )}
                <button
                  role="tab"
                  type="button"
                  aria-selected={name === active}
                  aria-label={folder ? `${name.slice(slash + 1)} (${FOLDER_LABELS[folder] ?? folder})` : undefined}
                  title={folder ? name : undefined}
                  onClick={() => setActive(name)}
                  className={`rounded px-3 py-1 font-mono text-sm ${name === active ? 'bg-surface font-bold text-ink' : 'text-muted'}`}
                >
                  {folder ? name.slice(slash + 1) : name}
                </button>
              </span>
            );
          })}
        </div>
        <div className="ml-auto flex items-center gap-1 text-sm">
          <span className="text-muted" aria-hidden="true">Text size</span>
          <button type="button" className="rounded px-2 font-bold" aria-label="Smaller code text" onClick={() => setFontSize((s) => Math.max(12, s - 2))}>A−</button>
          <button type="button" className="rounded px-2 font-bold" aria-label="Bigger code text" onClick={() => setFontSize((s) => Math.min(28, s + 2))}>A+</button>
        </div>
      </div>

      <div className="min-h-0 flex-1">
        <Editor
          path={modelPrefix + active}
          language={languageFor(active)}
          value={files[active] ?? ''}
          theme={dark ? 'vs-dark' : 'vs'}
          onChange={(value) => onChange?.({ ...files, [active]: value ?? '' })}
          options={{
            readOnly,
            fontSize,
            minimap: { enabled: false },
            wordWrap: 'on',
            // C# code is indented with 4 spaces by convention (and in every C# example the learner will find).
            tabSize: active.endsWith('.cs') ? 4 : 2,
            scrollBeyondLastLine: false,
            automaticLayout: true,
            accessibilitySupport: 'auto',
            ariaLabel: `${label}: ${active}`,
            // Autocomplete pop-ups distract beginners more than they help; quick suggestions stay off.
            quickSuggestions: false,
            renderLineHighlight: 'line',
            lineNumbersMinChars: 3,
          }}
        />
      </div>

      {!readOnly && (
        <p className="border-t border-line px-3 py-1 text-xs text-muted">
          Keyboard tip: the Tab key types spaces inside the editor. Press Ctrl+M (Mac: Ctrl+Shift+M) to make Tab move to the next button instead.
        </p>
      )}
    </div>
  );
}
