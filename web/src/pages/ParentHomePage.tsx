import { useQuery } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { Link } from 'react-router-dom';
import { api, ApiError } from '../api/client';
import { useChildren, useCreateChild } from '../api/queries';
import type { ChildSummary, CreateChildResponse } from '../api/types';
import { ErrorNote, Field, MONTHS, PageTitle, Spinner } from '../components/ui';

export function ParentHomePage() {
  const { data: children, error, isLoading } = useChildren();
  const [adding, setAdding] = useState(false);
  const [created, setCreated] = useState<CreateChildResponse | null>(null);

  return (
    <div className="space-y-6">
      <PageTitle sub="See progress, set limits and manage consent for each child.">Your family</PageTitle>

      {created && <CreatedNotice created={created} onClose={() => setCreated(null)} />}

      {isLoading && <Spinner />}
      <ErrorNote error={error} />

      {children && children.length === 0 && !adding && (
        <section className="card space-y-3">
          <h2 className="text-xl font-bold">Add your first child</h2>
          <p className="text-muted">You'll choose their username and password. If they're under 13, we'll email you to confirm consent.</p>
          <button type="button" className="btn-primary" onClick={() => setAdding(true)}>Add a child</button>
        </section>
      )}

      <ul className="grid gap-4 md:grid-cols-2">
        {children?.map((c) => <ChildCard key={c.id} child={c} />)}
      </ul>

      {children && children.length > 0 && !adding && (
        <button type="button" className="btn-secondary" onClick={() => setAdding(true)}>Add another child</button>
      )}

      {adding && <AddChildForm onDone={(r) => { setAdding(false); if (r) setCreated(r); }} />}
    </div>
  );
}

function statusText(c: ChildSummary): { text: string; tone: 'ok' | 'warn' | 'bad' } {
  if (c.status === 'PendingConsent') return { text: 'Waiting for your consent: check your email', tone: 'warn' };
  if (c.status === 'Suspended') return { text: 'Turned off (consent withdrawn)', tone: 'bad' };
  return { text: 'Active', tone: 'ok' };
}

function ChildCard({ child }: { child: ChildSummary }) {
  const status = statusText(child);
  const tone = { ok: 'text-ok', warn: 'text-accent', bad: 'text-bad' }[status.tone];
  return (
    <li className="card space-y-3">
      <div className="flex items-start justify-between gap-2">
        <div>
          <h2 className="text-xl font-bold">{child.userName}</h2>
          <p className="text-sm text-muted">{child.ageBand === 'Explorer' ? 'Explorer (10–13 lessons)' : 'Builder (14–17 lessons)'}</p>
        </div>
        <p className={`text-sm font-bold ${tone}`}>{status.text}</p>
      </div>
      <dl className="grid grid-cols-3 gap-2 text-center">
        <div><dt className="text-xs text-muted">Lessons done</dt><dd className="text-2xl font-bold">{child.lessonsCompleted}</dd></div>
        <div><dt className="text-xs text-muted">Minutes, last 7 days</dt><dd className="text-2xl font-bold">{child.minutesLast7Days}</dd></div>
        <div><dt className="text-xs text-muted">Level</dt><dd className="text-2xl font-bold">{child.level}</dd></div>
      </dl>
      <Link to={`/parent/children/${child.id}`} className="btn-secondary w-full">
        Progress and settings<span className="sr-only"> for {child.userName}</span>
      </Link>
    </li>
  );
}

function AddChildForm({ onDone }: { onDone: (r: CreateChildResponse | null) => void }) {
  const [form, setForm] = useState({ userName: '', password: '', birthMonth: '', birthYear: '' });
  const mutation = useCreateChild();
  const suggestions = useQuery({
    queryKey: ['username-suggestions'],
    queryFn: () => api.get<string[]>('/api/parent/username-suggestions'),
    staleTime: Infinity,
  });
  const err = mutation.error instanceof ApiError ? mutation.error : null;
  const thisYear = new Date().getFullYear();
  const years = Array.from({ length: 12 }, (_, i) => thisYear - 7 - i);

  function submit(e: FormEvent) {
    e.preventDefault();
    mutation.mutate(
      { userName: form.userName, password: form.password, birthMonth: Number(form.birthMonth), birthYear: Number(form.birthYear) },
      { onSuccess: (r) => onDone(r) },
    );
  }

  return (
    <form onSubmit={submit} className="card space-y-4" noValidate aria-labelledby="add-child-h">
      <h2 id="add-child-h" className="text-xl font-bold">Add a child</h2>

      <div className="space-y-2">
        <Field
          label="Username"
          hint="Others may see this, so please don't use your child's real name. Pick a suggestion or make one up."
          value={form.userName}
          onChange={(e) => setForm({ ...form, userName: e.target.value })}
          error={err?.fieldError('userName')}
          autoComplete="off"
          required
        />
        {suggestions.data && (
          <div className="flex flex-wrap gap-2" role="group" aria-label="Username suggestions">
            {suggestions.data.map((s) => (
              <button key={s} type="button" className="rounded-full border border-line px-3 py-1 text-sm" onClick={() => setForm({ ...form, userName: s })}>
                {s}
              </button>
            ))}
          </div>
        )}
      </div>

      <Field
        label="Password"
        type="text"
        hint="At least 8 characters. Shown here so you can write it down for your child."
        value={form.password}
        onChange={(e) => setForm({ ...form, password: e.target.value })}
        error={err?.fieldError('password')}
        autoComplete="off"
        required
      />

      <fieldset className="space-y-2">
        <legend className="font-bold">Birth month and year</legend>
        <p className="text-sm text-muted">We only use this to choose lessons for their age and to know when they turn 13.</p>
        <div className="flex gap-2">
          <label className="sr-only" htmlFor="cbm">Birth month</label>
          <select id="cbm" className="input" value={form.birthMonth} onChange={(e) => setForm({ ...form, birthMonth: e.target.value })}>
            <option value="">Month</option>
            {MONTHS.map((m, i) => <option key={m} value={i + 1}>{m}</option>)}
          </select>
          <label className="sr-only" htmlFor="cby">Birth year</label>
          <select id="cby" className="input" value={form.birthYear} onChange={(e) => setForm({ ...form, birthYear: e.target.value })}>
            <option value="">Year</option>
            {years.map((y) => <option key={y} value={y}>{y}</option>)}
          </select>
        </div>
        {err?.fieldError('birthYear') && <p className="text-sm font-bold text-bad">{err.fieldError('birthYear')}</p>}
      </fieldset>

      {!err?.problem.errors && <ErrorNote error={mutation.error} />}
      <div className="flex gap-2">
        <button type="submit" className="btn-primary" disabled={mutation.isPending}>Create account</button>
        <button type="button" className="btn-secondary" onClick={() => onDone(null)}>Cancel</button>
      </div>
    </form>
  );
}

function CreatedNotice({ created, onClose }: { created: CreateChildResponse; onClose: () => void }) {
  return (
    <section role="status" className="card space-y-2 border-brand">
      <h2 className="text-xl font-bold">{created.child.userName}'s account is ready{created.consentRequired ? ', almost' : ''}!</h2>
      {created.consentRequired ? (
        <p>We've emailed you a link. Open it and confirm consent, and then {created.child.userName} can sign in.</p>
      ) : (
        <p>{created.child.userName} can sign in now with the username and password you chose.</p>
      )}
      {created.devConfirmUrl && (
        <p className="text-sm">
          <strong>Development only:</strong>{' '}
          <a className="link" href={new URL(created.devConfirmUrl).pathname + new URL(created.devConfirmUrl).search}>open the consent link</a>
        </p>
      )}
      <button type="button" className="btn-secondary" onClick={onClose}>OK</button>
    </section>
  );
}
