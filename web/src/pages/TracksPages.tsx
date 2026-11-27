import { useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useMe, useTrack, useTracks } from '../api/queries';
import { ErrorNote, PageTitle, ProgressBar, Spinner } from '../components/ui';

export function TracksPage() {
  const { data: me } = useMe();
  const isLearner = me?.roles.includes('Learner');
  const [band, setBand] = useState<'Explorer' | 'Builder' | ''>('');
  const { data: tracks, error, isLoading } = useTracks(isLearner ? undefined : band || undefined);
  // Prerequisites arrive as slugs; show each track's title instead ("TypeScript", not "typescript").
  const titles = new Map(tracks?.map((t) => [t.slug, t.title]));

  return (
    <div>
      <PageTitle sub="Tracks are paths of courses, in order. Finish a track to earn a certificate.">Tracks</PageTitle>

      {!isLearner && (
        <fieldset className="mb-6 flex flex-wrap gap-2">
          <legend className="mb-2 font-bold">Show lessons for</legend>
          {([['', 'Everyone'], ['Explorer', 'Explorers (10–13)'], ['Builder', 'Builders (14–17)']] as const).map(([value, label]) => (
            <label key={value} className={`btn-secondary cursor-pointer ${band === value ? 'border-brand' : ''}`}>
              <input type="radio" name="band" className="sr-only" checked={band === value} onChange={() => setBand(value)} />
              {label}
            </label>
          ))}
        </fieldset>
      )}

      {isLoading && <Spinner />}
      <ErrorNote error={error} />
      <ol className="grid gap-4 md:grid-cols-2">
        {tracks?.map((t, i) => (
          <li key={t.slug}>
            <Link to={`/tracks/${t.slug}`} className="card block h-full space-y-2 no-underline hover:bg-surface-2">
              <p className="text-sm font-bold text-muted">Track {i + 1} · {t.courseCount} course{t.courseCount === 1 ? '' : 's'}</p>
              <h2 className="text-2xl font-bold">{t.title}</h2>
              <p className="text-muted">{t.description}</p>
              {t.prerequisites.length > 0 && <p className="text-sm">Best after: {t.prerequisites.map((slug) => titles.get(slug) ?? slug.replace(/-/g, ' ')).join(', ')}</p>}
            </Link>
          </li>
        ))}
      </ol>
    </div>
  );
}

export function TrackPage() {
  const { slug = '' } = useParams();
  const { data: track, error, isLoading } = useTrack(slug);
  if (isLoading) return <Spinner />;
  if (error || !track) return <ErrorNote error={error} />;

  const pickOne = track.completionRule === 'AnyCourse';
  const CourseList = pickOne ? 'ul' : 'ol';

  return (
    <div className="space-y-6">
      <div>
        <Link to="/tracks" className="link text-sm">← All tracks</Link>
        <PageTitle sub={track.description}>{track.title}</PageTitle>
      </div>

      {track.prerequisites.length > 0 && (
        <p className="card">
          Best to finish first:{' '}
          {track.prerequisites.map((p, i) => (
            <span key={p.slug}>{i > 0 && ', '}<Link to={`/tracks/${p.slug}`} className="link">{p.title}</Link></span>
          ))}
        </p>
      )}

      {track.certificateCode && (
        <p className="card border-brand font-bold">
          You finished this track! <Link to={`/certificates/${track.certificateCode}`} className="link">View your certificate</Link>
        </p>
      )}

      {pickOne && (
        <p className="card">
          Pick the one you like best. Each course builds the same kind of app with different data, and
          finishing any one of them finishes this track.
        </p>
      )}

      {/* An ordered list when the courses are a sequence; a plain list when they're a choice. */}
      <CourseList className="space-y-4">
        {track.courses.map((c, i) => (
          <li key={c.slug} className="card flex flex-wrap items-center gap-4">
            <span className="flex h-10 w-10 items-center justify-center rounded-full bg-surface-2 text-lg font-bold" aria-hidden="true">
              {pickOne ? '★' : i + 1}
            </span>
            <div className="min-w-0 flex-1 space-y-1">
              <h2 className="text-xl font-bold">
                <Link to={`/courses/${c.slug}`} className="hover:underline">{c.title}</Link>
              </h2>
              <p className="text-muted">{c.summary}</p>
              {c.isPublished && c.lessonCount > 0 ? (
                <ProgressBar label={`${c.title} progress`} value={c.lessonsCompleted} max={c.lessonCount} />
              ) : (
                <p className="text-sm font-bold text-accent">Coming soon</p>
              )}
            </div>
          </li>
        ))}
      </CourseList>
    </div>
  );
}
