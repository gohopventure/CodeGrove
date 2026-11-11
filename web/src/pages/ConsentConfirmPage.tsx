import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { api } from '../api/client';
import { keys } from '../api/queries';
import { ErrorNote, PageTitle } from '../components/ui';

/**
 * The "plus" step of email-plus consent: the parent arrives from the emailed link, signed in,
 * reads the direct notice and actively agrees. Keep this notice in sync with App:ConsentNoticeVersion.
 */
export function ConsentConfirmPage() {
  const [params] = useSearchParams();
  const token = params.get('token') ?? '';
  const [agree, setAgree] = useState(false);
  const [status, setStatus] = useState<'idle' | 'saving' | 'done'>('idle');
  const [error, setError] = useState<unknown>(null);
  const qc = useQueryClient();

  async function confirm() {
    setStatus('saving');
    setError(null);
    try {
      await api.post('/api/consent/confirm', { token, agreeToNotice: agree });
      await qc.invalidateQueries({ queryKey: keys.children });
      setStatus('done');
    } catch (e) {
      setError(e);
      setStatus('idle');
    }
  }

  if (status === 'done') {
    return (
      <div className="card mx-auto max-w-xl space-y-4">
        <h1 className="text-2xl font-bold">Thank you! Your child can sign in now.</h1>
        <p>They'll use the username and password you chose. You can change settings or withdraw consent at any time.</p>
        <Link to="/parent" className="btn-primary">Go to your family dashboard</Link>
      </div>
    );
  }

  return (
    <div className="mx-auto max-w-2xl">
      <PageTitle>Parental consent</PageTitle>
      <section className="card space-y-4">
        <h2 className="text-xl font-bold">What we collect from your child, and why</h2>
        <ul className="list-disc space-y-1 pl-6">
          <li><strong>Username and password</strong> you chose, so they can sign in.</li>
          <li><strong>Birth month and year</strong>, to pick lessons for their age and know when they turn 13.</li>
          <li><strong>Learning activity</strong>: lessons finished, code they write, quiz answers, hints used and time spent, so we can save their progress and show it to you.</li>
        </ul>
        <h2 className="text-xl font-bold">What we never do</h2>
        <ul className="list-disc space-y-1 pl-6">
          <li>Ask your child for an email, real name, photo, school or location.</li>
          <li>Show ads or use tracking for advertising.</li>
          <li>Share or sell your child's information to anyone.</li>
          <li>Let other users message your child or see a profile.</li>
        </ul>
        <h2 className="text-xl font-bold">Your rights</h2>
        <p>
          From your dashboard you can see everything we store, set a daily time limit, withdraw consent and delete your child's
          account and data. We keep submitted code for 12 months. Accounts unused for 18 months are deleted after we email you.
        </p>
        <label className="flex items-start gap-3 rounded-card border border-line p-3">
          <input type="checkbox" className="mt-1 h-5 w-5" checked={agree} onChange={(e) => setAgree(e.target.checked)} />
          <span>I am this child's parent or legal guardian, I have read this notice, and I consent to the collection and use described above.</span>
        </label>
        <ErrorNote error={error} />
        <button type="button" className="btn-primary" disabled={!agree || !token || status === 'saving'} onClick={confirm}>
          Give consent
        </button>
        {!token && <p className="font-bold text-bad">This link is missing its code. Open the link from the email again.</p>}
      </section>
    </div>
  );
}
