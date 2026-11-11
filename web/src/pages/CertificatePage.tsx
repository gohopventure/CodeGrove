import { useParams } from 'react-router-dom';
import { useCertificate } from '../api/queries';
import { Spinner } from '../components/ui';

/** Printable certificate. Also the public verification page, so it shows only username, track and date. */
export function CertificatePage() {
  const { code = '' } = useParams();
  const { data: cert, isLoading, error } = useCertificate(code);

  if (isLoading) return <Spinner />;
  if (error || !cert) {
    return <p className="card font-bold">We couldn't find a certificate with the code {code}. Check the code and try again.</p>;
  }

  return (
    <div className="space-y-4">
      <article className="mx-auto max-w-3xl border-8 border-double border-brand bg-white p-10 text-center text-[#1d2320]">
        <p className="text-lg uppercase tracking-widest">Codegrove certificate of completion</p>
        <h1 className="my-6 text-4xl font-bold">{cert.trackTitle}</h1>
        <p className="text-lg">This certifies that</p>
        <p className="my-3 text-3xl font-bold">{cert.userName}</p>
        <p className="text-lg">completed every course in the {cert.trackTitle} track.</p>
        <p className="mt-8">
          {new Date(cert.issuedAt).toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric' })}
        </p>
        <p className="mt-2 font-mono text-sm">Verification code: {cert.code}</p>
      </article>
      <div className="no-print text-center">
        <button type="button" className="btn-primary" onClick={() => window.print()}>Print certificate</button>
      </div>
    </div>
  );
}
