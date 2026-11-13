import type { Tutor, TutorMood } from '../api/types';

/**
 * Who the tutor is comes from the API (Content/tutors.json): Juni for Explorers, Rowan for Builders. Scripts never
 * say the tutor's name, so renaming a character only changes that file.
 *
 * Later, the avatar tutor can replace the portrait with pre-rendered or live video in TutorPanel; nothing else in the
 * app needs to know.
 */
export type TutorCharacter = NonNullable<Tutor['character']>;

/** Used only if no character is set up for the learner's band. */
export const FALLBACK_CHARACTER: TutorCharacter = {
  slug: 'guide',
  name: 'Your guide',
  portraitUrl: '/tutor/placeholder.svg',
};

/**
 * Shown under the name everywhere the tutor appears. The tutor is a character, never a person, and the UI says so
 * (Character & Behavior Spec, shared rules).
 */
export const AI_CHARACTER_LABEL = 'Your tutor (AI character)';

/** The portrait for a mood, e.g. "/tutor/juni-{mood}.svg" + Celebrating → "/tutor/juni-celebrating.svg". */
export function portraitFor(character: TutorCharacter, mood: TutorMood = 'Neutral'): string {
  return character.portraitUrl.replace('{mood}', mood.toLowerCase());
}
