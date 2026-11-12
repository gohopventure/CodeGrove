/*
 * Blocks mode for Explorer JavaScript lessons: a Blockly workspace plus a read-only view of the
 * JavaScript the blocks make, so learners see block ↔ text side by side before switching to Text.
 *
 * Loaded lazily (React.lazy) from the lesson player, so Blockly only downloads for lessons that use it.
 * Blockly 13 provides keyboard navigation and screen-reader support itself; Text mode is always one
 * click away as the alternative.
 *
 * Privacy: Blockly's sounds and cursors come from /blockly-media (copied from node_modules/blockly/media),
 * never from Blockly's default CDN.
 */
import { useEffect, useRef } from 'react';
import { Blockly, workspaceToScript } from './generator';
import { toolboxFor } from './toolbox';

export interface BlocksEditorProps {
  toolbox: readonly string[];
  /** Workspace state to start from. Read on mount (and when the theme changes); later changes come from the user. */
  initialState: object | null;
  onChange: (state: object, script: string) => void;
  dark: boolean;
}

const lightTheme = Blockly.Theme.defineTheme('cg-light', {
  name: 'cg-light',
  base: Blockly.Themes.Classic,
  componentStyles: { workspaceBackgroundColour: '#fbfaf7', toolboxBackgroundColour: '#f1efe9', flyoutBackgroundColour: '#e7e4dc' },
});

const darkTheme = Blockly.Theme.defineTheme('cg-dark', {
  name: 'cg-dark',
  base: Blockly.Themes.Classic,
  componentStyles: {
    workspaceBackgroundColour: '#1b2128',
    toolboxBackgroundColour: '#262d35',
    toolboxForegroundColour: '#e8edf2',
    flyoutBackgroundColour: '#2f3741',
    flyoutForegroundColour: '#e8edf2',
    flyoutOpacity: 0.95,
    scrollbarColour: '#5a6573',
    insertionMarkerColour: '#ffffff',
  },
});

export default function BlocksEditor({ toolbox, initialState, onChange, dark }: BlocksEditorProps) {
  const host = useRef<HTMLDivElement>(null);
  const onChangeRef = useRef(onChange);
  onChangeRef.current = onChange;
  // The latest state survives a re-inject (theme change) without depending on the parent re-rendering.
  const stateRef = useRef<object | null>(initialState);
  const toolboxKey = toolbox.join(',');

  useEffect(() => {
    const el = host.current;
    if (!el) return;

    // The app's font setting (including the dyslexia-friendly option) applies to blocks too.
    const fontFamily = getComputedStyle(el).fontFamily;
    const theme = dark ? darkTheme : lightTheme;
    theme.setFontStyle({ family: fontFamily, size: 13 });

    const workspace = Blockly.inject(el, {
      toolbox: toolboxFor(toolboxKey.split(',')),
      theme,
      renderer: 'zelos', // chunky, Scratch-like blocks: bigger targets for younger learners
      media: '/blockly-media/',
      trashcan: true,
      zoom: { controls: true, wheel: false, startScale: 0.8, maxScale: 2, minScale: 0.5 },
      // The mouse wheel scrolls the lesson page, not the workspace (dragging the background still pans it),
      // so learners can't get stuck scrolling inside the blocks area.
      move: { scrollbars: true, drag: true, wheel: false },
    });

    if (stateRef.current) {
      try {
        Blockly.serialization.workspaces.load(stateRef.current, workspace);
      } catch {
        // A draft from an older version of the lesson; start from an empty workspace instead.
        workspace.clear();
      }
    }

    // Dev-only handles for end-to-end checks. Never present in production builds.
    if (import.meta.env.DEV) Object.assign(window, { __cgBlocks: workspace, __cgBlockly: Blockly });

    const emit = () => {
      const state = Blockly.serialization.workspaces.save(workspace);
      stateRef.current = state;
      onChangeRef.current(state, workspaceToScript(workspace));
    };
    emit();
    workspace.addChangeListener((e: Blockly.Events.Abstract) => {
      // UI events (selection, scrolling, opening the toolbox) don't change the program. During a drag the
      // block isn't connected yet, so wait for the drop.
      if (e.isUiEvent || workspace.isDragging()) return;
      emit();
    });

    const resize = new ResizeObserver(() => Blockly.svgResize(workspace));
    resize.observe(el);

    return () => {
      resize.disconnect();
      workspace.dispose();
    };
  }, [toolboxKey, dark]);

  return <div ref={host} className="h-full w-full" />;
}
