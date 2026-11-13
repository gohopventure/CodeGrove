import { useCallback, useEffect, useRef, useState } from 'react';
import type { Tutor, TutorEvent, TutorLine } from '../api/types';

/**
 * When the tutor speaks up during a challenge (Character & Behavior Spec, "Reusable event lines"):
 *
 * - FirstFail: the first failed check gets a short reassurance. No hint unless the learner asks.
 * - StuckOffer: after 3 failed checks, or about 4 minutes without progress, the tutor offers a hint. It only offers,
 *   and never once the learner has already opened a hint or the solution (they found the help on their own).
 * - Pass: a short celebration of the work.
 *
 * The tutor never interrupts while the learner is typing: a line waits until typing has paused.
 */
export const STUCK_AFTER_FAILS = 3;
export const STUCK_AFTER_MS = 4 * 60 * 1000;
export const TYPING_PAUSE_MS = 2500;

export interface ChallengeTutorState {
  fails: number;
  /** A stuck offer is made at most once per visit to the challenge, so it never nags. */
  offered: boolean;
  passed: boolean;
  /** The learner opened a hint or the solution, so there's nothing left to offer. */
  helped: boolean;
}

export const initialTutorState: ChallengeTutorState = { fails: 0, offered: false, passed: false, helped: false };

/** What a check result makes the tutor say, if anything. Pure, so it's easy to test. */
export function afterCheck(state: ChallengeTutorState, passed: boolean): { state: ChallengeTutorState; event: TutorEvent | null } {
  if (passed) return { state: { ...state, passed: true }, event: state.passed ? null : 'pass' };
  const fails = state.fails + 1;
  if (fails === 1) return { state: { ...state, fails }, event: 'firstFail' };
  if (fails >= STUCK_AFTER_FAILS && !state.offered && !state.passed && !state.helped) return { state: { ...state, fails, offered: true }, event: 'stuckOffer' };
  return { state: { ...state, fails }, event: null };
}

/** Whether the time rule should make a stuck offer now. */
export function stuckByTime(state: ChallengeTutorState, msSinceProgress: number): boolean {
  return !state.offered && !state.passed && !state.helped && msSinceProgress >= STUCK_AFTER_MS;
}

/** Picks a line from a pool at random, never the same one twice in a row. */
export function pickLine(pool: readonly TutorLine[] | undefined, last: TutorLine | null, random = Math.random): TutorLine | null {
  if (!pool || pool.length === 0) return null;
  const choices = pool.length > 1 && last ? pool.filter((l) => l !== last) : pool;
  return choices[Math.floor(random() * choices.length)] ?? null;
}

/**
 * Drives the tutor during a challenge. Call `checked` after every check, `typed` on every edit, and `progressed`
 * when more checks pass than before. `line` is what the tutor is saying now (null for nothing).
 */
export function useChallengeTutor(tutor: Tutor, { quiet, alreadyPassed }: { quiet: boolean; alreadyPassed: boolean }) {
  const state = useRef<ChallengeTutorState>({ ...initialTutorState, passed: alreadyPassed });
  const lastProgress = useRef(Date.now());
  const lastTyped = useRef(0);
  const lastLine = useRef<TutorLine | null>(null);
  const [pending, setPending] = useState<{ event: TutorEvent; line: TutorLine } | null>(null);
  const [current, setCurrent] = useState<{ event: TutorEvent; line: TutorLine } | null>(null);

  const say = useCallback((event: TutorEvent | null) => {
    if (!event || quiet) return;
    const line = pickLine(tutor.lines[event], lastLine.current);
    if (!line) return;
    lastLine.current = line;
    setPending({ event, line });
  }, [quiet, tutor.lines]);

  // Show a pending line once the learner has stopped typing for a moment.
  useEffect(() => {
    if (!pending) return;
    const flush = () => {
      if (Date.now() - lastTyped.current >= TYPING_PAUSE_MS) {
        setCurrent(pending);
        setPending(null);
        return true;
      }
      return false;
    };
    if (flush()) return;
    const t = window.setInterval(flush, 500);
    return () => window.clearInterval(t);
  }, [pending]);

  // The "about 4 minutes without progress" rule.
  useEffect(() => {
    const t = window.setInterval(() => {
      if (stuckByTime(state.current, Date.now() - lastProgress.current)) {
        state.current = { ...state.current, offered: true };
        say('stuckOffer');
      }
    }, 15_000);
    return () => window.clearInterval(t);
  }, [say]);

  const checked = useCallback((passed: boolean) => {
    const next = afterCheck(state.current, passed);
    state.current = next.state;
    say(next.event);
  }, [say]);

  const typed = useCallback(() => {
    lastTyped.current = Date.now();
  }, []);

  const progressed = useCallback(() => {
    lastProgress.current = Date.now();
  }, []);

  const dismiss = useCallback(() => setCurrent(null), []);

  /** The learner opened a hint or the solution: drop any offer on screen or waiting, and make no more. */
  const helped = useCallback(() => {
    state.current = { ...state.current, helped: true };
    setPending((p) => (p?.event === 'stuckOffer' ? null : p));
    setCurrent((c) => (c?.event === 'stuckOffer' ? null : c));
  }, []);

  return { current: quiet ? null : current, checked, typed, progressed, dismiss, helped };
}
