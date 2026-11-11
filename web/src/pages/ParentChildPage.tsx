import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useState, type FormEvent } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { api } from '../api/client';
import { keys, useChildProgress, useChildSettings } from '../api/queries';
import type { ChildProgress, ChildSettings } from '../api/types';
import { ErrorNote, Field, PageTitle, ProgressBar, Spinner } from '../components/ui';

export function ParentChildPage() {
  const { childId = '' } = useParams();
  const { data, error, isLoading } = useChildProgress(childId);

  if (isLoading) return <Spinner />;
  if (error || !data) return <ErrorNote error={error} />;

  return (
    <div className="space-y-6">
      <div>
        <Link to="/parent" className="link text-sm">← Your family</Link>
        <PageTitle sub={`Level ${data.child.level} · ${data.child.xp} XP · ${plural(data.child.lessonsCompleted, 'lesson')} finished`}>
          {data.child.userName}
        </PageTitle>
      </div>

      <TimeSpentChart days={data.last14Days} limit={data.settings.dailyMinutesLimit} />

      <section aria-labelledby="courses-h" className="card space-y-4">
        <h2 id="courses-h" className="text-xl font-bold">Courses</h2>
        {data.courses.length === 0 && <p className="text-muted">No lessons started yet.</p>}
        {data.courses.map((c) => (
          <div key={c.slug} className="space-y-2">
            <div className="flex justify-between">
              <h3 className="font-bold">{c.title}</h3>
              <span className="text-sm text-muted">{c.lessonsCompleted} of {plural(c.lessonsTotal, 'lesson')} ready so far</span>
            </div>
            <ProgressBar label={`${c.title} progress`} value={c.lessonsCompleted} max={c.lessonsTotal} />
            <table className="w-full text-left text-sm">
              <caption className="sr-only">Lessons in {c.title}</caption>
              <thead>
                <tr className="border-b border-line text-muted">
                  <th scope="col" className="py-1">Lesson</th>
                  <th scope="col">Challenge</th>
                  <th scope="col">Best quiz score</th>
                  <th scope="col">Hints used</th>
                </tr>
              </thead>
              <tbody>
                {c.lessons.map((l) => (
                  <tr key={l.lessonId} className="border-b border-line">
                    <th scope="row" className="py-1 font-normal">{l.title}{l.completedAt && ' ✓'}</th>
                    <td>{l.challengePassed ? 'Passed' : 'Not yet'}</td>
                    <td>{l.bestQuizScore != null ? `${l.bestQuizScore}%` : '–'}</td>
                    <td>{l.hintsUsed}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ))}
        {data.badges.length > 0 && <p><strong>Badges:</strong> {data.badges.join(', ')}</p>}
      </section>

      <SettingsForm childId={childId} initial={data.settings} />
      <AccountActions childId={childId} data={data} />
    </div>
  );
}

/** 14-day bar chart drawn with plain SVG, plus a data table for screen readers. */
function TimeSpentChart({ days, limit }: { days: ChildProgress['last14Days']; limit: number | null }) {
  const max = Math.max(limit ?? 0, 15, ...days.map((d) => d.minutes));
  const w = 640, h = 160, pad = 24, barW = (w - pad * 2) / days.length - 6;
  const fmt = (iso: string) => new Date(`${iso}T00:00:00`).toLocaleDateString(undefined, { weekday: 'short', day: 'numeric' });

  return (
    <section aria-labelledby="time-h" className="card space-y-3">
      <h2 id="time-h" className="text-xl font-bold">Time spent, last 14 days</h2>
      {days.every((d) => d.minutes === 0) && <p className="text-muted">No learning time recorded in the last 14 days yet. Time counts while a lesson is open on screen.</p>}
      <svg viewBox={`0 0 ${w} ${h + 28}`} className="w-full" aria-hidden="true">
        {limit != null && (
          <>
            <line x1={pad} x2={w - pad} y1={h - (limit / max) * (h - pad)} y2={h - (limit / max) * (h - pad)} stroke="var(--accent)" strokeDasharray="4 4" />
            <text x={w - pad} y={h - (limit / max) * (h - pad) - 4} textAnchor="end" fontSize="11" fill="var(--muted)">daily limit {limit} min</text>
          </>
        )}
        {days.map((d, i) => {
          const bh = (d.minutes / max) * (h - pad);
          const x = pad + i * ((w - pad * 2) / days.length) + 3;
          return (
            <g key={d.date}>
              <rect x={x} y={h - bh} width={barW} height={Math.max(bh, 1)} rx="3" fill="var(--brand)" />
              {i % 2 === 1 && <text x={x + barW / 2} y={h + 16} textAnchor="middle" fontSize="11" fill="var(--muted)">{fmt(d.date)}</text>}
            </g>
          );
        })}
        <line x1={pad} x2={w - pad} y1={h} y2={h} stroke="var(--line)" />
      </svg>
      <details>
        <summary className="cursor-pointer text-sm font-bold">Show as a table</summary>
        <table className="mt-2 text-sm">
          <thead><tr><th scope="col" className="pr-4 text-left">Day</th><th scope="col" className="pr-4 text-left">Minutes</th><th scope="col" className="text-left">Lessons finished</th></tr></thead>
          <tbody>
            {days.map((d) => <tr key={d.date}><td className="pr-4">{fmt(d.date)}</td><td className="pr-4">{d.minutes}</td><td>{d.lessonsCompleted}</td></tr>)}
          </tbody>
        </table>
      </details>
    </section>
  );
}

const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? '' : 's'}`;

function SettingsForm({ childId, initial }: { childId: string; initial: ChildSettings }) {
  const [limitOn, setLimitOn] = useState(initial.dailyMinutesLimit != null);
  const [limit, setLimit] = useState(String(initial.dailyMinutesLimit ?? 45));
  const [goal, setGoal] = useState(initial.weeklyGoalDays);
  const [digest, setDigest] = useState(initial.weeklyDigest);
  const [saved, setSaved] = useState(false);
  const save = useChildSettings(childId);

  useEffect(() => setSaved(false), [limitOn, limit, goal, digest]);

  function submit(e: FormEvent) {
    e.preventDefault();
    save.mutate(
      { dailyMinutesLimit: limitOn ? Number(limit) : null, weeklyGoalDays: goal, weeklyDigest: digest, allowAiTutor: false, allowCommunity: false },
      { onSuccess: () => setSaved(true) },
    );
  }

  return (
    <form onSubmit={submit} className="card space-y-4" aria-labelledby="settings-h">
      <h2 id="settings-h" className="text-xl font-bold">Settings</h2>
      <label className="flex items-center gap-3">
        <input type="checkbox" className="h-5 w-5" checked={limitOn} onChange={(e) => setLimitOn(e.target.checked)} />
        <span className="font-bold">Daily time limit</span>
      </label>
      {limitOn && <Field label="Minutes per day" type="number" min={5} max={600} value={limit} onChange={(e) => setLimit(e.target.value)} />}
      <div className="space-y-1">
        <label htmlFor="goal" className="font-bold">Weekly goal</label>
        <p className="text-sm text-muted">Streaks count weeks that hit this goal, so a missed day never resets anything.</p>
        <select id="goal" className="input" value={goal} onChange={(e) => setGoal(Number(e.target.value))}>
          {[1, 2, 3, 4, 5, 6, 7].map((n) => <option key={n} value={n}>{n} day{n === 1 ? '' : 's'} a week</option>)}
        </select>
      </div>
      <label className="flex items-center gap-3">
        <input type="checkbox" className="h-5 w-5" checked={digest} onChange={(e) => setDigest(e.target.checked)} />
        <span>Email me a weekly progress summary</span>
      </label>
      <p className="text-sm text-muted">AI tutor and community features aren't available yet. When they are, they'll be off until you turn them on.</p>
      <ErrorNote error={save.error} />
      <div className="flex items-center gap-3">
        <button type="submit" className="btn-primary" disabled={save.isPending}>Save settings</button>
        {saved && <span role="status" className="font-bold text-ok">Saved</span>}
      </div>
    </form>
  );
}

function AccountActions({ childId, data }: { childId: string; data: ChildProgress }) {
  const [password, setPassword] = useState('');
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  const qc = useQueryClient();
  const navigate = useNavigate();
  const name = data.child.userName;

  async function act(fn: () => Promise<unknown>, done: string) {
    setError(null);
    setMessage(null);
    try {
      await fn();
      setMessage(done);
    } catch (e) {
      setError(e);
    }
  }

  return (
    <section aria-labelledby="account-h" className="card space-y-5">
      <h2 id="account-h" className="text-xl font-bold">Account</h2>

      <form
        className="flex flex-wrap items-end gap-2"
        onSubmit={(e) => {
          e.preventDefault();
          void act(() => api.post(`/api/parent/children/${childId}/password`, { newPassword: password }), `${name}'s password was changed.`).then(() => setPassword(''));
        }}
      >
        <div className="min-w-60 flex-1">
          <Field label={`New password for ${name}`} type="text" autoComplete="off" value={password} onChange={(e) => setPassword(e.target.value)} hint="At least 8 characters." />
        </div>
        <button type="submit" className="btn-secondary" disabled={password.length < 8}>Change password</button>
      </form>

      {data.child.status !== 'Suspended' && (
        <div className="space-y-1">
          <h3 className="font-bold">Withdraw consent</h3>
          <p className="text-sm text-muted">{name} won't be able to sign in. Their data is deleted after 7 days unless you contact us.</p>
          <button
            type="button"
            className="btn-danger"
            onClick={() =>
              window.confirm(`Withdraw consent for ${name}? They'll be signed out.`) &&
              act(async () => {
                await api.post(`/api/parent/children/${childId}/consent/revoke`);
                await qc.invalidateQueries({ queryKey: keys.child(childId) });
              }, 'Consent withdrawn.')
            }
          >
            Withdraw consent
          </button>
        </div>
      )}

      <div className="space-y-1">
        <h3 className="font-bold">Delete account and data</h3>
        <p className="text-sm text-muted">Permanently deletes {name}'s account, code, progress and badges. This can't be undone.</p>
        <button
          type="button"
          className="btn-danger"
          onClick={() =>
            window.confirm(`Permanently delete ${name}'s account and all their data?`) &&
            act(async () => {
              await api.del(`/api/parent/children/${childId}`);
              await qc.invalidateQueries({ queryKey: keys.children });
              navigate('/parent');
            }, 'Deleted.')
          }
        >
          Delete {name}'s account
        </button>
      </div>

      {message && <p role="status" className="font-bold text-ok">{message}</p>}
      <ErrorNote error={error} />
    </section>
  );
}
