/**
 * Guards the tutor's content against the Avatar Tutor Character & Behavior Spec. The API's seeder enforces the same
 * rules (TutorRules.cs) and refuses to start on a violation; this test finds them faster, with clearer messages.
 *
 * Scripts (Content/tutor/<course>.json): every published lesson has an intro and a recap. Each segment is written
 * twice: `displayText` for the screen (may use `code`) and `speechText` for a voice (plain words: "u-l", "console dot
 * log", "C sharp"). Explorer intros and recaps are 2–3 sentences, Builder ones 3–4.
 *
 * Hints (Content/courses/<course>.json): level 1 is a guiding question, every hint has speechText and a mood, and no
 * hint contains a whole solution file (the solution is a separate step after hint 3). Hint and event lines: Explorer
 * at most 2 sentences, Builder at most 3.
 *
 * Characters (Content/tutors.json): one per band, 3+ firstFail lines, 3+ stuckOffer lines that ask, 8–10 pass lines.
 */
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

const contentDir = resolve(process.cwd(), '../src/Codegrove.Api/Content');

type Band = 'Explorer' | 'Builder' | 'Both';
const MOODS = ['Neutral', 'Encouraging', 'Thinking', 'Celebrating'];

interface Line { displayText: string; speechText: string; mood: string }
interface Hint { level: number; markdown: string; speechText?: string; mood?: string }
interface CourseFile {
  slug: string;
  isPublished: boolean;
  lessons: {
    slug: string;
    isPublished: boolean;
    ageBand: Band;
    challenge?: { solutionFiles: Record<string, string>; hints: Hint[] } | null;
  }[];
}
interface TutorFile { course: string; lessons: Record<string, { intro: Line[]; recap: Line[] }> }
interface TutorsFile { characters: { slug: string; name: string; band: Band; portraitUrl: string; lines: Record<string, Line[]> }[] }

const courses = readdirSync(`${contentDir}/courses`)
  .filter((f) => f.endsWith('.json'))
  .map((f) => JSON.parse(readFileSync(`${contentDir}/courses/${f}`, 'utf8')) as CourseFile)
  .filter((c) => c.isPublished);

const tutorFor = (course: string): TutorFile | null => {
  const path = `${contentDir}/tutor/${course}.json`;
  return existsSync(path) ? (JSON.parse(readFileSync(path, 'utf8')) as TutorFile) : null;
};

/** A sentence ends with . ! or ? followed by whitespace or the end ("1.5" isn't an end). */
const sentences = (text: string) => (text.trim().match(/[.!?]+(?=\s|$)/g) ?? []).length;
const NOT_SPOKEN = /[`<>{}[\]*#_=|\\;]|https?:|:\/\/|\b(e\.g|i\.e|etc)\./i;
const squash = (s: string) => s.replace(/\s+/g, '');

function expectSpoken(speech: string | undefined, where: string, min: number, max: number) {
  expect(speech?.trim(), `${where}: needs speechText`).toBeTruthy();
  expect(speech!.match(NOT_SPOKEN)?.[0] ?? null, `${where}: speechText has text a voice can't read: "${speech}"`).toBeNull();
  const n = sentences(speech!);
  expect(n >= min && n <= max, `${where}: speechText should be ${min}–${max} sentences, not ${n}: "${speech}"`).toBe(true);
}

function expectLine(line: Line, where: string, min: number, max: number) {
  expect(line.displayText?.trim(), `${where}: needs displayText`).toBeTruthy();
  expect(MOODS, `${where}: mood`).toContain(line.mood);
  expectSpoken(line.speechText, where, min, max);
}

const scriptRange = (band: Band): [number, number] => (band === 'Explorer' ? [2, 3] : band === 'Builder' ? [3, 4] : [2, 4]);
const maxLine = (band: Band) => (band === 'Explorer' ? 2 : 3);

const lessons = courses.flatMap((course) =>
  course.lessons.filter((l) => l.isPublished).map((lesson) => ({ name: `${course.slug} → ${lesson.slug}`, course, lesson })),
);

describe('tutor scripts', () => {
  it('finds published lessons to check', () => expect(lessons.length).toBeGreaterThan(0));

  it.each(lessons)('$name', ({ course, lesson }) => {
    const script = tutorFor(course.slug)?.lessons[lesson.slug];
    expect(script, `add an intro and recap for ${lesson.slug} to Content/tutor/${course.slug}.json`).toBeTruthy();

    const [min, max] = scriptRange(lesson.ageBand);
    for (const kind of ['intro', 'recap'] as const) {
      const lines = script![kind];
      expect(lines?.length ?? 0, `${kind} is missing`).toBeGreaterThan(0);
      lines.forEach((l, i) => expectLine(l, `${kind} ${i + 1}`, 1, max));
      expectSpoken(lines.map((l) => l.speechText).join(' '), kind, min, max);
    }
  });

  it('has no scripts for lessons that do not exist', () => {
    for (const course of courses) {
      const known = new Set(course.lessons.map((l) => l.slug));
      const extra = Object.keys(tutorFor(course.slug)?.lessons ?? {}).filter((slug) => !known.has(slug));
      expect(extra, `unknown lessons in tutor/${course.slug}.json`).toEqual([]);
    }
  });
});

describe('hint ladders', () => {
  const withChallenges = lessons.filter((x) => x.lesson.challenge);

  it.each(withChallenges)('$name', ({ lesson }) => {
    const { hints, solutionFiles } = lesson.challenge!;
    expect(hints.map((h) => h.level).sort()).toEqual([1, 2, 3]);
    for (const h of hints) {
      const where = `hint ${h.level}`;
      expect(MOODS, `${where}: mood`).toContain(h.mood);
      expectSpoken(h.speechText, where, 1, maxLine(lesson.ageBand));
      for (const [name, code] of Object.entries(solutionFiles)) {
        const file = squash(code);
        if (file.length >= 40) expect(squash(h.markdown).includes(file), `${where} contains the whole solution ${name}`).toBe(false);
      }
    }
    const first = hints.find((h) => h.level === 1)!;
    expect(first.markdown.trim().endsWith('?') && first.speechText!.trim().endsWith('?'), `hint 1 should be a guiding question: "${first.markdown}"`).toBe(true);
  });
});

describe('tutor characters', () => {
  const file = JSON.parse(readFileSync(`${contentDir}/tutors.json`, 'utf8')) as TutorsFile;

  it('has one tutor per band, Juni and Rowan', () => {
    expect(file.characters.map((c) => [c.band, c.name])).toEqual([['Explorer', 'Juni'], ['Builder', 'Rowan']]);
    for (const c of file.characters) {
      expect(c.portraitUrl).toContain('{mood}');
      for (const mood of MOODS) {
        const path = resolve(process.cwd(), 'public', c.portraitUrl.replace('{mood}', mood.toLowerCase()).replace(/^\//, ''));
        expect(existsSync(path), `missing portrait ${path}`).toBe(true);
      }
    }
  });

  it.each(file.characters.map((c) => ({ name: c.name, c })))('$name event lines', ({ c }) => {
    const max = maxLine(c.band);
    const pools = { firstFail: [3, 10], stuckOffer: [3, 10], pass: [8, 10] } as const;
    for (const [event, [lo, hi]] of Object.entries(pools)) {
      const lines = c.lines[event] ?? [];
      expect(lines.length >= lo && lines.length <= hi, `${c.name} needs ${lo}–${hi} ${event} lines, not ${lines.length}`).toBe(true);
      lines.forEach((l, i) => expectLine(l, `${c.name} ${event} ${i + 1}`, 1, max));
    }
    for (const l of c.lines.stuckOffer ?? []) expect(l.speechText.trim().endsWith('?'), `stuck offers only ask: "${l.speechText}"`).toBe(true);
    // Praise the work, not the kid; never claim to be a person or a friend.
    const all = Object.values(c.lines).flat().map((l) => `${l.displayText} ${l.speechText}`.toLowerCase());
    for (const text of all) {
      expect(text, 'no "wrong"').not.toMatch(/\bwrong\b/);
      expect(text, 'no person/friend claims').not.toMatch(/\b(i'm your friend|i missed you|genius|you're so smart)\b/);
    }
  });
});
