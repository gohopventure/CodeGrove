import { Link, Navigate } from 'react-router-dom';
import { useMe } from '../api/queries';
import { homeFor } from '../components/RequireRole';

export function LandingPage() {
  const { data: me } = useMe();
  if (me) return <Navigate to={homeFor(me.roles)} replace />;

  return (
    <div className="space-y-12">
      <section className="grid items-center gap-8 md:grid-cols-2">
        <div className="space-y-5">
          <h1 className="text-4xl font-bold leading-tight md:text-5xl">Grow from your first web page to a full-stack app.</h1>
          <p className="text-lg text-muted">
            Short lessons, real code in your browser, and projects you can be proud of. Made for kids and teens aged 10 to 17.
          </p>
          <div className="flex flex-wrap gap-3">
            <Link to="/signup/parent" className="btn-primary">I'm a parent: set up my child</Link>
            <Link to="/signup/teen" className="btn-secondary">I'm 13 or older</Link>
          </div>
        </div>
        <div className="card font-mono text-sm" aria-hidden="true">
          <p className="text-muted">&lt;!-- your first lesson --&gt;</p>
          <p>&lt;<span className="text-brand">h1</span>&gt;Welcome to my page&lt;/<span className="text-brand">h1</span>&gt;</p>
          <p>&lt;<span className="text-brand">p</span>&gt;Octopuses have three hearts.&lt;/<span className="text-brand">p</span>&gt;</p>
        </div>
      </section>

      <section aria-labelledby="how" className="grid gap-4 md:grid-cols-3">
        <h2 id="how" className="sr-only">How it works</h2>
        {[
          ['Learn a little', 'Lessons take 3 to 15 minutes, with every new word explained.'],
          ['Code it yourself', 'Type real HTML, CSS and JavaScript and see your page change as you go.'],
          ['Get helpful hints', "Stuck? Hints come one step at a time, and they're always free."],
        ].map(([title, body]) => (
          <div key={title} className="card">
            <h3 className="text-xl font-bold">{title}</h3>
            <p className="text-muted">{body}</p>
          </div>
        ))}
      </section>

      <section aria-labelledby="families" className="card space-y-2">
        <h2 id="families" className="text-2xl font-bold">Built for families</h2>
        <ul className="list-disc space-y-1 pl-6">
          <li>No ads, no public profiles and no chat with strangers.</li>
          <li>Children under 13 join only through a parent account, with your consent.</li>
          <li>See progress and time spent, set a daily limit, and delete your child's data whenever you want.</li>
        </ul>
        <Link to="/privacy" className="link">Read how we handle your family's data</Link>
      </section>
    </div>
  );
}
