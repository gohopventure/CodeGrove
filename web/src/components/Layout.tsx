import { useEffect } from 'react';
import { Link, NavLink, Outlet, useNavigate } from 'react-router-dom';
import { useLogout, useMe } from '../api/queries';
import type { Me } from '../api/types';

/** Applies the audience theme and accessibility preferences to <html>. */
function useDocumentTheme(me: Me | null | undefined) {
  useEffect(() => {
    const root = document.documentElement;
    const band = me?.ageBand === 'Explorer' ? 'explorer' : me?.ageBand === 'Builder' ? 'builder' : 'grownups';
    root.dataset.band = band;
    root.dataset.reducedMotion = String(!!me?.reducedMotion);
    root.classList.toggle('dyslexic', !!me?.useDyslexiaFont);
  }, [me]);
}

export function Layout() {
  const { data: me } = useMe();
  const logout = useLogout();
  const navigate = useNavigate();
  useDocumentTheme(me);

  const navClass = ({ isActive }: { isActive: boolean }) =>
    `rounded-card px-3 py-2 font-bold ${isActive ? 'bg-surface-2 text-ink' : 'text-muted hover:text-ink'}`;

  return (
    <div className="flex min-h-screen flex-col">
      <a href="#main" className="sr-only focus:not-sr-only focus:absolute focus:left-2 focus:top-2 focus:z-50 btn-primary">
        Skip to main content
      </a>

      <header className="border-b border-line bg-surface">
        <nav aria-label="Main" className="mx-auto flex max-w-6xl flex-wrap items-center gap-2 px-4 py-3">
          <Link to={me ? (me.roles.includes('Parent') ? '/parent' : '/learn') : '/'} className="mr-4 flex items-center gap-2 text-xl font-bold no-underline">
            <img src="/favicon.svg" alt="" width={32} height={32} />
            Codegrove
          </Link>

          {me?.roles.includes('Learner') && (
            <>
              <NavLink to="/learn" className={navClass}>Home</NavLink>
              <NavLink to="/tracks" className={navClass}>Tracks</NavLink>
              <NavLink to="/me/badges" className={navClass}>Badges</NavLink>
              <NavLink to="/me/portfolio" className={navClass}>Portfolio</NavLink>
            </>
          )}
          {me?.roles.includes('Parent') && (
            <>
              <NavLink to="/parent" className={navClass}>Family</NavLink>
              <NavLink to="/tracks" className={navClass}>What kids learn</NavLink>
            </>
          )}
          {!me && <NavLink to="/tracks" className={navClass}>What kids learn</NavLink>}

          <div className="ml-auto flex items-center gap-2">
            {me ? (
              <>
                <NavLink to="/settings" className={navClass}>
                  <span className="sr-only">Settings for </span>
                  {me.roles.includes('Parent') ? 'Settings' : me.userName}
                </NavLink>
                <button
                  type="button"
                  className="btn-secondary"
                  onClick={() => logout.mutate(undefined, { onSettled: () => navigate('/') })}
                >
                  Log out
                </button>
              </>
            ) : (
              <>
                <Link to="/login" className="btn-secondary">Log in</Link>
                <Link to="/signup" className="btn-primary">Sign up</Link>
              </>
            )}
          </div>
        </nav>
      </header>

      <main id="main" tabIndex={-1} className="mx-auto w-full max-w-6xl flex-1 px-4 py-8 focus:outline-none">
        <Outlet />
      </main>

      <footer className="border-t border-line px-4 py-6 text-center text-sm text-muted">
        No ads. No public profiles. No messages from strangers.{' '}
        <Link to="/privacy" className="link">Privacy for families</Link>
      </footer>
    </div>
  );
}
