import { Navigate, Outlet, useLocation } from 'react-router-dom';
import { useMe } from '../api/queries';
import type { Role } from '../api/types';
import { Spinner } from './ui';

/** Route guard. The API enforces every rule too; this only keeps people out of pages they can't use. */
export function RequireRole({ role }: { role?: Role }) {
  const { data: me, isLoading } = useMe();
  const location = useLocation();

  if (isLoading) return <Spinner />;
  if (!me) return <Navigate to={`/login?next=${encodeURIComponent(location.pathname + location.search)}`} replace />;
  if (role && !me.roles.includes(role)) return <Navigate to={homeFor(me.roles)} replace />;
  return <Outlet />;
}

export function homeFor(roles: Role[]): string {
  if (roles.includes('Learner')) return '/learn';
  if (roles.includes('Parent')) return '/parent';
  return '/';
}
