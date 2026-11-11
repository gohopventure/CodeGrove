import { useState, type FormEvent } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import { useLogin } from '../api/queries';
import { homeFor } from '../components/RequireRole';
import { ErrorNote, Field, PageTitle } from '../components/ui';

export function LoginPage() {
  const [login, setLogin] = useState('');
  const [password, setPassword] = useState('');
  const mutation = useLogin();
  const navigate = useNavigate();
  const [params] = useSearchParams();

  function submit(e: FormEvent) {
    e.preventDefault();
    mutation.mutate(
      { login, password },
      {
        onSuccess: (me) => {
          const next = params.get('next');
          // Only follow local paths, never another site.
          navigate(next?.startsWith('/') && !next.startsWith('//') ? next : homeFor(me.roles), { replace: true });
        },
      },
    );
  }

  return (
    <div className="mx-auto max-w-md">
      <PageTitle>Welcome back</PageTitle>
      <form onSubmit={submit} className="card space-y-4" noValidate>
        <Field label="Username or email" autoComplete="username" value={login} onChange={(e) => setLogin(e.target.value)} required />
        <Field label="Password" type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} required />
        <ErrorNote error={mutation.error} />
        <button type="submit" className="btn-primary w-full" disabled={mutation.isPending}>
          {mutation.isPending ? 'Logging in…' : 'Log in'}
        </button>
        <p className="text-sm text-muted">
          Forgot your password? If a grown-up set up your account, they can reset it from their dashboard.
        </p>
      </form>
      <p className="mt-4 text-center">
        New here? <Link to="/signup" className="link">Create an account</Link>
      </p>
    </div>
  );
}
