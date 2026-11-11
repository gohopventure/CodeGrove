import { useState, type FormEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { ApiError } from '../api/client';
import { useRegister } from '../api/queries';
import { ErrorNote, Field, MONTHS, PageTitle } from '../components/ui';

export function SignupChoicePage() {
  return (
    <div className="mx-auto max-w-3xl">
      <PageTitle>Create an account</PageTitle>
      <div className="grid gap-4 md:grid-cols-2">
        <Link to="/signup/parent" className="card block space-y-2 no-underline hover:bg-surface-2">
          <h2 className="text-xl font-bold">I'm a parent or guardian</h2>
          <p className="text-muted">Create your account, then add your children. Kids under 13 must join this way.</p>
        </Link>
        <Link to="/signup/teen" className="card block space-y-2 no-underline hover:bg-surface-2">
          <h2 className="text-xl font-bold">I'm 13 or older</h2>
          <p className="text-muted">Set up your own learner account. You can link a parent later.</p>
        </Link>
      </div>
    </div>
  );
}

export function SignupParentPage() {
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [confirmAdult, setConfirmAdult] = useState(false);
  const mutation = useRegister('parent');
  const navigate = useNavigate();
  const err = mutation.error instanceof ApiError ? mutation.error : null;

  function submit(e: FormEvent) {
    e.preventDefault();
    mutation.mutate({ email, password, confirmAdult }, { onSuccess: () => navigate('/parent', { replace: true }) });
  }

  return (
    <div className="mx-auto max-w-md">
      <PageTitle sub="Next, you'll add your child and confirm consent by email.">Parent sign-up</PageTitle>
      <form onSubmit={submit} className="card space-y-4" noValidate>
        <Field label="Your email" type="email" autoComplete="email" value={email} onChange={(e) => setEmail(e.target.value)} error={err?.fieldError('email')} required />
        <Field label="Password" type="password" autoComplete="new-password" hint="At least 8 characters." value={password} onChange={(e) => setPassword(e.target.value)} error={err?.fieldError('password')} required minLength={8} />
        <label className="flex items-start gap-3">
          <input type="checkbox" className="mt-1 h-5 w-5" checked={confirmAdult} onChange={(e) => setConfirmAdult(e.target.checked)} />
          <span>I am 18 or older and a parent or legal guardian.</span>
        </label>
        {err?.fieldError('confirmAdult') && <p className="text-sm font-bold text-bad">{err.fieldError('confirmAdult')}</p>}
        {!err?.problem.errors && <ErrorNote error={mutation.error} />}
        <button type="submit" className="btn-primary w-full" disabled={mutation.isPending}>Create parent account</button>
      </form>
    </div>
  );
}

/**
 * Neutral age screen (COPPA): month and year pickers with no default and no hint of which answers
 * pass. Under-13s are sent to ask a grown-up; the API also sets a cookie so the form can't be retried.
 */
export function SignupTeenPage() {
  const [form, setForm] = useState({ userName: '', email: '', password: '', birthMonth: '', birthYear: '' });
  const mutation = useRegister('teen');
  const navigate = useNavigate();
  const err = mutation.error instanceof ApiError ? mutation.error : null;
  const gated = err?.status === 422 && !err.problem.errors;
  const thisYear = new Date().getFullYear();
  const years = Array.from({ length: 90 }, (_, i) => thisYear - i);
  const set = (k: keyof typeof form) => (e: { target: { value: string } }) => setForm({ ...form, [k]: e.target.value });

  function submit(e: FormEvent) {
    e.preventDefault();
    mutation.mutate(
      { ...form, birthMonth: Number(form.birthMonth), birthYear: Number(form.birthYear) },
      { onSuccess: () => navigate('/learn', { replace: true }) },
    );
  }

  if (gated) {
    return (
      <div className="card mx-auto max-w-md space-y-4 text-center">
        <h1 className="text-2xl font-bold">Let's get a grown-up</h1>
        <p>{err.message}</p>
        <Link to="/" className="btn-primary">OK</Link>
      </div>
    );
  }

  return (
    <div className="mx-auto max-w-md">
      <PageTitle>Create your learner account</PageTitle>
      <form onSubmit={submit} className="card space-y-4" noValidate>
        <fieldset className="space-y-2">
          <legend className="font-bold">When were you born?</legend>
          <div className="flex gap-2">
            <label className="sr-only" htmlFor="bm">Birth month</label>
            <select id="bm" className="input" value={form.birthMonth} onChange={set('birthMonth')} required>
              <option value="">Month</option>
              {MONTHS.map((m, i) => <option key={m} value={i + 1}>{m}</option>)}
            </select>
            <label className="sr-only" htmlFor="by">Birth year</label>
            <select id="by" className="input" value={form.birthYear} onChange={set('birthYear')} required>
              <option value="">Year</option>
              {years.map((y) => <option key={y} value={y}>{y}</option>)}
            </select>
          </div>
          {err?.fieldError('birthYear') && <p className="text-sm font-bold text-bad">{err.fieldError('birthYear')}</p>}
        </fieldset>
        <Field
          label="Username"
          hint="This is the only name other people might see. Don't use your real name."
          autoComplete="username"
          value={form.userName}
          onChange={set('userName')}
          error={err?.fieldError('userName')}
          required
        />
        <Field label="Email" type="email" autoComplete="email" value={form.email} onChange={set('email')} error={err?.fieldError('email')} required />
        <Field label="Password" type="password" autoComplete="new-password" hint="At least 8 characters." value={form.password} onChange={set('password')} error={err?.fieldError('password')} required />
        {!err?.problem.errors && <ErrorNote error={mutation.error} />}
        <button type="submit" className="btn-primary w-full" disabled={mutation.isPending}>Create my account</button>
      </form>
    </div>
  );
}
