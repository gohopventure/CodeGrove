import { describe, expect, it } from 'vitest';
import type { TutorLine } from '../api/types';
import { afterCheck, initialTutorState, pickLine, STUCK_AFTER_MS, stuckByTime } from './tutorEvents';

describe('tutor event rules', () => {
  it('reassures on the first fail, offers a hint on the third, and never again', () => {
    let s = initialTutorState;
    const events: (string | null)[] = [];
    for (let i = 0; i < 5; i++) {
      const r = afterCheck(s, false);
      s = r.state;
      events.push(r.event);
    }
    expect(events).toEqual(['firstFail', null, 'stuckOffer', null, null]);
  });

  it('celebrates a pass once', () => {
    const first = afterCheck(initialTutorState, true);
    expect(first.event).toBe('pass');
    expect(afterCheck(first.state, true).event).toBeNull();
  });

  it('offers a hint after about 4 minutes without progress, unless passed or already offered', () => {
    expect(stuckByTime(initialTutorState, STUCK_AFTER_MS - 1)).toBe(false);
    expect(stuckByTime(initialTutorState, STUCK_AFTER_MS)).toBe(true);
    expect(stuckByTime({ ...initialTutorState, offered: true }, STUCK_AFTER_MS)).toBe(false);
    expect(stuckByTime({ ...initialTutorState, passed: true }, STUCK_AFTER_MS)).toBe(false);
  });

  it('makes no stuck offer once the learner has opened a hint or the solution', () => {
    let s = { ...initialTutorState, helped: true };
    const events: (string | null)[] = [];
    for (let i = 0; i < 4; i++) {
      const r = afterCheck(s, false);
      s = r.state;
      events.push(r.event);
    }
    expect(events).toEqual(['firstFail', null, null, null]);
    expect(stuckByTime(s, STUCK_AFTER_MS * 2)).toBe(false);
  });

  it('never picks the same line twice in a row', () => {
    const pool: TutorLine[] = ['a', 'b', 'c'].map((t) => ({ displayText: t, speechText: t, mood: 'Celebrating' }));
    let last: TutorLine | null = null;
    for (let i = 0; i < 50; i++) {
      const next = pickLine(pool, last);
      expect(next).not.toBe(last);
      last = next;
    }
    expect(pickLine([], null)).toBeNull();
    expect(pickLine(undefined, null)).toBeNull();
  });
});
