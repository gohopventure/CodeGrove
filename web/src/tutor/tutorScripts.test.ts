/**
 * Guards the tutor's scripts (src/Codegrove.Api/Content/tutor/<course>.json). Every published lesson needs
 * a spoken-style intro and recap, written so a voice (text-to-speech today, an avatar later) can read them
 * without stumbling:
 *
 * - 2–4 sentences and at most 75 words per intro or recap
 * - plain words only: no markdown, code symbols or URLs (say "console dot log", "the h1 tag", "triple equals")
 * - no abbreviations like "e.g." that a voice would read letter by letter
 *
 * The API only warns about a lesson without a script (so a course in progress still starts); this test fails.
 */
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

const contentDir = resolve(process.cwd(), '../src/Codegrove.Api/Content');

interface CourseFile {
  slug: string;
  isPublished: boolean;
  lessons: { slug: string; isPublished: boolean }[];
}
interface TutorFile {
  course: string;
  lessons: Record<string, { intro: string[]; recap: string[] }>;
}

const courses = readdirSync(`${contentDir}/courses`)
  .filter((f) => f.endsWith('.json'))
  .map((f) => JSON.parse(readFileSync(`${contentDir}/courses/${f}`, 'utf8')) as CourseFile)
  .filter((c) => c.isPublished);

const tutorFor = (course: string): TutorFile | null => {
  const path = `${contentDir}/tutor/${course}.json`;
  return existsSync(path) ? (JSON.parse(readFileSync(path, 'utf8')) as TutorFile) : null;
};

/** Sentences end with . ! or ? followed by a space or the end of the text. */
const sentences = (text: string) => text.split(/(?<=[.!?])\s+/).filter((s) => s.trim().length > 0).length;
const words = (text: string) => text.split(/\s+/).filter(Boolean).length;
const NOT_SPOKEN = /[`<>{}[\]*#_=|\\;]|https?:|:\/\/|\b(e\.g|i\.e|etc)\./i;

const cases = courses.flatMap((course) =>
  course.lessons.filter((l) => l.isPublished).map((lesson) => ({ name: `${course.slug} → ${lesson.slug}`, course, lesson })),
);

describe('tutor scripts', () => {
  it('finds published lessons to check', () => expect(cases.length).toBeGreaterThan(0));

  it.each(cases)('$name', ({ course, lesson }) => {
    const script = tutorFor(course.slug)?.lessons[lesson.slug];
    expect(script, `add an intro and recap for ${lesson.slug} to Content/tutor/${course.slug}.json`).toBeTruthy();

    for (const kind of ['intro', 'recap'] as const) {
      const lines = script![kind];
      expect(lines?.length ?? 0, `${kind} is missing`).toBeGreaterThan(0);
      const text = lines.join(' ');
      expect(sentences(text), `${kind} should be 2–4 sentences: "${text}"`).toBeGreaterThanOrEqual(2);
      expect(sentences(text), `${kind} should be 2–4 sentences: "${text}"`).toBeLessThanOrEqual(4);
      expect(words(text), `${kind} should be at most 75 words`).toBeLessThanOrEqual(75);
      expect(text.match(NOT_SPOKEN)?.[0] ?? null, `${kind} has text a voice can't read naturally: "${text}"`).toBeNull();
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
