import Editor from '@monaco-editor/react';
import { useState } from 'react';
import type { Files } from '../api/types';
import './monacoSetup';

const LANGUAGE: Record<string, string> = { html: 'html', css: 'css', js: 'javascript', ts: 'typescript', json: 'json' };
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
}: {
  files: Files;
  onChange?: (files: Files) => void;
  dark: boolean;
  readOnly?: boolean;
  label?: string;
}) {
  const names = Object.keys(files);
  const [active, setActive] = useState(names[0] ?? 'index.html');
  const [fontSize, setFontSize] = useState(16);

  return (
    <div className="flex h-full min-h-[320px] flex-col overflow-hidden rounded-card border border-line bg-surface">
      <div className="flex items-center gap-1 border-b border-line bg-surface-2 px-2 py-1">
        <div role="tablist" aria-label="Files" className="flex gap-1">
          {names.map((name) => (
            <button
              key={name}
              role="tab"
              type="button"
              aria-selected={name === active}
              onClick={() => setActive(name)}
              className={`rounded px-3 py-1 font-mono text-sm ${name === active ? 'bg-surface font-bold text-ink' : 'text-muted'}`}
            >
              {name}
            </button>
          ))}
        </div>
        <div className="ml-auto flex items-center gap-1 text-sm">
          <span className="text-muted" aria-hidden="true">Text size</span>
          <button type="button" className="rounded px-2 font-bold" aria-label="Smaller code text" onClick={() => setFontSize((s) => Math.max(12, s - 2))}>A−</button>
          <button type="button" className="rounded px-2 font-bold" aria-label="Bigger code text" onClick={() => setFontSize((s) => Math.min(28, s + 2))}>A+</button>
        </div>
      </div>

      <div className="min-h-0 flex-1">
        <Editor
          path={active}
          language={languageFor(active)}
          value={files[active] ?? ''}
          theme={dark ? 'vs-dark' : 'vs'}
          onChange={(value) => onChange?.({ ...files, [active]: value ?? '' })}
          options={{
            readOnly,
            fontSize,
            minimap: { enabled: false },
            wordWrap: 'on',
            tabSize: 2,
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
