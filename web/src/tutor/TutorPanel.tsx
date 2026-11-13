import type { ReactNode } from 'react';
import { TUTOR } from './character';

/**
 * The tutor slot: the character's picture and name with what they're saying, as text for now.
 * `lines` are spoken-style script segments (intro or recap); `children` can hold interactive content such
 * as the hint ladder. Swapping the picture for avatar video later only changes this component.
 */
export function TutorPanel({
  label,
  lines = [],
  children,
  className = '',
}: {
  /** What the tutor is doing, for screen readers and as the heading, e.g. "Before you start". */
  label: string;
  lines?: readonly string[];
  children?: ReactNode;
  className?: string;
}) {
  if (lines.length === 0 && !children) return null;
  return (
    <aside aria-label={`${TUTOR.name}: ${label}`} className={`card space-y-3 ${className}`}>
      <div className="flex items-center gap-3">
        <img src={TUTOR.picture} alt="" width={56} height={56} className="h-14 w-14 shrink-0 rounded-full" />
        <div>
          <p className="font-bold leading-tight">{TUTOR.name}</p>
          <p className="text-sm text-muted">{label}</p>
        </div>
      </div>
      {lines.length > 0 && (
        <div className="relative rounded-card bg-surface-2 p-4 text-lg leading-relaxed">
          {lines.map((line, i) => (
            <p key={i} className={i > 0 ? 'mt-2' : undefined}>{line}</p>
          ))}
        </div>
      )}
      {children}
    </aside>
  );
}
