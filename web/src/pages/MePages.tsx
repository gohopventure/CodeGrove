import { Link } from 'react-router-dom';
import { useBadges, useDashboard } from '../api/queries';
import { ErrorNote, PageTitle, Spinner } from '../components/ui';

export function BadgesPage() {
  const { data: badges, error, isLoading } = useBadges();
  if (isLoading) return <Spinner />;
  return (
    <div>
      <PageTitle sub="Badges celebrate what you've done. There's no rush and no way to lose them.">Badge shelf</PageTitle>
      <ErrorNote error={error} />
      <ul className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
        {badges?.map((b) => (
          <li key={b.code} className={`card flex items-center gap-4 ${b.earned ? '' : 'opacity-60'}`}>
            <img src={b.iconUrl} alt="" width={64} height={64} className={b.earned ? '' : 'grayscale'} />
            <div>
              <h2 className="font-bold">{b.name}</h2>
              <p className="text-sm text-muted">{b.description}</p>
              <p className="text-sm font-bold">{b.earned ? `Earned ${new Date(b.awardedAt!).toLocaleDateString()}` : 'Not yet'}</p>
            </div>
          </li>
        ))}
      </ul>
    </div>
  );
}

export function CertificatesPage() {
  const { data, error, isLoading } = useDashboard();
  if (isLoading) return <Spinner />;
  return (
    <div>
      <PageTitle sub="Finish every course in a track to earn its certificate.">Certificates</PageTitle>
      <ErrorNote error={error} />
      {data?.certificates.length === 0 && <p className="card">No certificates yet. Your first one is waiting at the end of Web Foundations.</p>}
      <ul className="space-y-3">
        {data?.certificates.map((c) => (
          <li key={c.code} className="card flex items-center justify-between">
            <span className="font-bold">{c.trackTitle}</span>
            <Link to={`/certificates/${c.code}`} className="btn-secondary">View and print</Link>
          </li>
        ))}
      </ul>
    </div>
  );
}
