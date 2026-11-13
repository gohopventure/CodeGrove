/**
 * The tutor character, in one place. The lesson player shows this name and picture next to the tutor's
 * lines (intro, hints, recap). When the character is chosen, change it here; scripts in
 * Content/tutor/*.json never say the tutor's name, so they don't need to change.
 *
 * Later, the avatar tutor can replace `picture` with live or pre-rendered video in TutorPanel; nothing
 * else in the app needs to know.
 */
export interface TutorCharacter {
  /** Shown as "<name> says". */
  name: string;
  /** Square image under /public. Decorative: the name is always shown as text. */
  picture: string;
  /** True until the real character is chosen. */
  placeholder: boolean;
}

export const TUTOR: TutorCharacter = {
  name: 'Your guide',
  picture: '/tutor/placeholder.svg',
  placeholder: true,
};
