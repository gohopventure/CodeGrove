import { useCallback, useSyncExternalStore } from 'react';

/**
 * "Quiet mode": the learner can always mute the tutor. Quiet hides the tutor's own lines (intro, recap and the
 * reassurances it says by itself); hints the learner asks for still show.
 *
 * Kept in this browser's localStorage. It's a per-learner comfort setting, not data anyone else needs, so it
 * doesn't go to the server. Storage can be unavailable (private mode, blocked storage), so every access is guarded
 * and falls back to "not quiet".
 */
const KEY = 'cg:tutor:quiet';
const listeners = new Set<() => void>();

function read(): boolean {
  try {
    return localStorage.getItem(KEY) === '1';
  } catch {
    return false;
  }
}

function write(quiet: boolean) {
  try {
    if (quiet) localStorage.setItem(KEY, '1');
    else localStorage.removeItem(KEY);
  } catch {
    // Storage blocked: the choice lasts until the page reloads.
    memory = quiet;
  }
  listeners.forEach((l) => l());
}

let memory: boolean | null = null;

function subscribe(listener: () => void) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

/** Every tutor panel on the page shares one setting. */
export function useQuietMode(): [boolean, (quiet: boolean) => void] {
  const quiet = useSyncExternalStore(subscribe, () => memory ?? read(), () => false);
  const set = useCallback((q: boolean) => write(q), []);
  return [quiet, set];
}
