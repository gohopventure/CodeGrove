import type { ReactNode } from 'react';
import type { TutorLine, TutorMood } from '../api/types';
import { Markdown } from '../components/Markdown';
import { AI_CHARACTER_LABEL, FALLBACK_CHARACTER, portraitFor, type TutorCharacter } from './character';
import { useQuietMode } from './quietMode';

/**
 * The tutor slot: the character's portrait and name with what they're saying, as text for now. The portrait follows
 * the mood of what's being said. `lines` are the tutor's own lines (intro, recap, event lines); `children` holds
 * things the learner asked for, like the hint ladder, which stay visible in quiet mode.
 *
 * Swapping the portrait for avatar video later only changes this component.
 */
export function TutorPanel({
  character,
  label,
  lines = [],
  mood,
  live = false,
  actions,
  children,
  className = '',
}: {
  character: TutorCharacter | null;
  /** What the tutor is doing, shown under the name and used for screen readers, e.g. "Before you start". */
  label: string;
  lines?: readonly TutorLine[];
  /** Overrides the mood of the lines, e.g. a revealed hint's mood. */
  mood?: TutorMood;
  /** Announce new lines politely to screen readers (for lines that appear on their own, like event lines). */
  live?: boolean;
  /** Buttons under the speech bubble, e.g. "Yes, a hint please". */
  actions?: ReactNode;
  children?: ReactNode;
  className?: string;
}) {
  const [quiet, setQuiet] = useQuietMode();
  const who = character ?? FALLBACK_CHARACTER;
  const showLines = !quiet && lines.length > 0;
  if (lines.length === 0 && !children) return null;

  const face = mood ?? lines.at(-1)?.mood ?? 'Neutral';

  return (
    <aside aria-label={`${who.name}, ${label}`} className={`card space-y-3 ${className}`}>
      {/* Header: portrait, name, then the AI-character label and what the tutor is doing on their own lines, so a
          narrow side column never squeezes them. Quiet mode is a small text button in the top corner. */}
      <div className="flex items-start gap-3">
        <img
          src={portraitFor(who, quiet ? 'Neutral' : face)}
          alt=""
          width={56}
          height={56}
          className={`shrink-0 rounded-full ${quiet ? 'h-10 w-10 opacity-70' : 'h-14 w-14'}`}
        />
        <div className="min-w-0 flex-1">
          <div className="flex items-baseline justify-between gap-2">
            <p className="font-bold leading-tight">{who.name}</p>
            {/* Icon-only toggle. The accessible name stays "Quiet mode" and aria-pressed says whether it's on, so
                screen readers hear "Quiet mode, toggle button, pressed". The tooltip says the same for mouse users. */}
            <button
              type="button"
              className="-mr-1 -mt-1 shrink-0 rounded-full p-1.5 text-muted hover:bg-surface-2 hover:text-ink focus-visible:outline-2"
              aria-label="Quiet mode"
              aria-pressed={quiet}
              title={quiet ? 'Quiet mode is on. Click to hear from ' + who.name + ' again.' : 'Quiet mode'}
              onClick={() => setQuiet(!quiet)}
            >
              <SpeakerIcon muted={quiet} />
            </button>
          </div>
          <p className="text-xs text-muted">{AI_CHARACTER_LABEL}</p>
          <p className="mt-1 text-sm font-bold text-muted">{label}</p>
        </div>
      </div>
      {quiet && lines.length > 0 && (
        <p className="text-sm text-muted">{who.name} is staying quiet. Turn quiet mode off to see what {who.name} says.</p>
      )}
      <div aria-live={live ? 'polite' : undefined}>
        {showLines && (
          <div className="rounded-card bg-surface-2 p-4 text-lg leading-relaxed">
            {lines.map((line, i) => (
              <Markdown key={i} className={i > 0 ? 'mt-2' : undefined}>{line.displayText}</Markdown>
            ))}
            {actions && <div className="mt-3 flex flex-wrap gap-2">{actions}</div>}
          </div>
        )}
      </div>
      {children}
    </aside>
  );
}

/** A small speaker icon, with a cross when muted. Drawn in currentColor so it follows the theme. */
function SpeakerIcon({ muted }: { muted: boolean }) {
  return (
    <svg viewBox="0 0 24 24" width="20" height="20" aria-hidden="true" focusable="false"
      fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M11 5 6 9H3v6h3l5 4V5z" fill="currentColor" />
      {muted ? (
        <path d="m16 9 6 6m0-6-6 6" />
      ) : (
        <path d="M15.5 8.5a5 5 0 0 1 0 7M18.5 5.5a9 9 0 0 1 0 13" />
      )}
    </svg>
  );
}
