import { Link } from 'react-router-dom';
import { useDashboard } from '../api/queries';
import { ErrorNote, ProgressBar, Spinner } from '../components/ui';

export function LearnHomePage() {
  const { data: d, error, isLoading } = useDashboard();
  if (isLoading) return <Spinner />;
  if (error || !d) return <ErrorNote error={error} />;

  const levelStart = d.xpForNextLevel - 50 * d.level; // XP needed for the current level
  const minutesToday = Math.floor(d.todaySeconds / 60);
  const explorer = d.ageBand === 'Explorer';

  return (
    <div className="space-y-6">
      <h1 className="text-3xl font-bold">{explorer ? `Hi, ${d.userName}! Ready to build?` : `Welcome back, ${d.userName}`}</h1>

      {d.continue ? (
        <section className="card flex flex-wrap items-center gap-4 border-brand">
          <div className="flex-1">
            <p className="text-sm font-bold text-muted">{d.lessonsCompleted === 0 ? 'Start here' : 'Pick up where you left off'}</p>
            <h2 className="text-2xl font-bold">{d.continue.lessonTitle}</h2>
            <p className="text-muted">{d.continue.courseTitle}</p>
          </div>
          <Link to={`/lessons/${d.continue.lessonId}`} className="btn-primary text-lg">
            {d.lessonsCompleted === 0 ? 'Start lesson' : 'Continue'} →
          </Link>
        </section>
      ) : (
        <section className="card">
          <h2 className="text-xl font-bold">You've finished every lesson that's ready!</h2>
          <p className="text-muted">New lessons are on the way. <Link to="/tracks" className="link">Browse the tracks</Link>.</p>
        </section>
      )}

      <div className="grid gap-4 md:grid-cols-3">
        <section className="card space-y-2" aria-labelledby="xp-h">
          <h2 id="xp-h" className="font-bold text-muted">Level {d.level}</h2>
          <p className="text-3xl font-bold">{d.xp} XP</p>
          <ProgressBar label="Progress to next level" value={d.xp - Math.max(0, levelStart)} max={d.xpForNextLevel - Math.max(0, levelStart)} />
          <p className="text-sm text-muted">{d.xpForNextLevel - d.xp} XP to level {d.level + 1}</p>
        </section>

        <section className="card space-y-2" aria-labelledby="goal-h">
          <h2 id="goal-h" className="font-bold text-muted">This week's goal</h2>
          <p className="text-3xl font-bold">{Math.min(d.daysActiveThisWeek, d.weeklyGoalDays)} of {d.weeklyGoalDays} days</p>
          <ProgressBar label="Weekly goal" value={d.daysActiveThisWeek} max={d.weeklyGoalDays} />
          <p className="text-sm text-muted">
            {d.streakWeeks > 0 ? `${d.streakWeeks} week${d.streakWeeks === 1 ? '' : 's'} in a row. ` : ''}
            Missing a day is fine. Weeks are what count.
          </p>
        </section>

        <section className="card space-y-2" aria-labelledby="today-h">
          <h2 id="today-h" className="font-bold text-muted">Today</h2>
          <p className="text-3xl font-bold">{minutesToday} min</p>
          {d.dailyMinutesLimit != null && (
            <p className="text-sm text-muted">Your daily time: {d.dailyMinutesLimit} minutes.</p>
          )}
          <p className="text-sm text-muted">{d.lessonsCompleted} lesson{d.lessonsCompleted === 1 ? '' : 's'} finished so far.</p>
        </section>
      </div>

      <section aria-labelledby="badges-h" className="card space-y-3">
        <div className="flex items-center justify-between">
          <h2 id="badges-h" className="text-xl font-bold">Your badges</h2>
          <Link to="/me/badges" className="link text-sm">See all badges</Link>
        </div>
        {d.badges.length === 0 ? (
          <p className="text-muted">Finish your first lesson to earn your first badge.</p>
        ) : (
          <ul className="flex flex-wrap gap-4">
            {d.badges.map((b) => (
              <li key={b.code} className="flex w-28 flex-col items-center text-center text-sm">
                <img src={b.iconUrl} alt="" width={64} height={64} />
                <span className="font-bold">{b.name}</span>
              </li>
            ))}
          </ul>
        )}
      </section>

      {d.certificates.length > 0 && (
        <section aria-labelledby="certs-h" className="card space-y-2">
          <h2 id="certs-h" className="text-xl font-bold">Certificates</h2>
          <ul className="space-y-1">
            {d.certificates.map((c) => (
              <li key={c.code}><Link to={`/certificates/${c.code}`} className="link">{c.trackTitle}</Link></li>
            ))}
          </ul>
        </section>
      )}
    </div>
  );
}
