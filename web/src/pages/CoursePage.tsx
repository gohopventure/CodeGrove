import { Link, useParams } from 'react-router-dom';
import { useCourse, useMe } from '../api/queries';
import { ErrorNote, PageTitle, Spinner } from '../components/ui';

export function CoursePage() {
  const { slug = '' } = useParams();
  const { data: course, error, isLoading } = useCourse(slug);
  const { data: me } = useMe();
  const isLearner = me?.roles.includes('Learner');

  if (isLoading) return <Spinner />;
  if (error || !course) return <ErrorNote error={error} />;

  return (
    <div className="grid gap-6 lg:grid-cols-[2fr_1fr]">
      <div>
        <PageTitle sub={`${course.summary} About ${course.estimatedMinutes} minutes.`}>{course.title}</PageTitle>
        <ol className="space-y-3">
          {course.lessons.map((l, i) => {
            const open = l.isPublished && isLearner;
            return (
              <li key={l.id} className="card flex items-center gap-4">
                <span
                  className={`flex h-9 w-9 shrink-0 items-center justify-center rounded-full font-bold ${l.completed ? 'bg-brand text-brand-ink' : 'bg-surface-2'}`}
                  aria-hidden="true"
                >
                  {l.completed ? '✓' : i + 1}
                </span>
                <div className="flex-1">
                  {open ? (
                    <Link to={`/lessons/${l.id}`} className="text-lg font-bold hover:underline">{l.title}</Link>
                  ) : (
                    <span className="text-lg font-bold">{l.title}</span>
                  )}
                  <p className="text-sm text-muted">
                    {l.completed && <span className="sr-only">Completed. </span>}
                    {l.isPublished ? `${l.estimatedMinutes} min` : 'Coming soon'}
                  </p>
                </div>
              </li>
            );
          })}
        </ol>
        {!isLearner && (
          <p className="mt-4 text-muted">
            Lessons open for signed-in learners. <Link to="/signup" className="link">Create an account</Link>
          </p>
        )}
      </div>
      <aside className="card h-fit space-y-2" aria-labelledby="obj-h">
        <h2 id="obj-h" className="text-xl font-bold">You'll be able to</h2>
        <ul className="list-disc space-y-1 pl-5">
          {course.objectives.map((o) => <li key={o}>{o}</li>)}
        </ul>
      </aside>
    </div>
  );
}
