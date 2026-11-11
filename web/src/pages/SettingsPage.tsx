import { useMe, usePreferences } from '../api/queries';
import { ErrorNote, PageTitle } from '../components/ui';

export function SettingsPage() {
  const { data: me } = useMe();
  const prefs = usePreferences();
  if (!me) return null;

  const toggle = (key: 'useDyslexiaFont' | 'reducedMotion') => (e: React.ChangeEvent<HTMLInputElement>) =>
    prefs.mutate({ useDyslexiaFont: me.useDyslexiaFont, reducedMotion: me.reducedMotion, [key]: e.target.checked });

  return (
    <div className="mx-auto max-w-xl">
      <PageTitle>Settings</PageTitle>
      <section className="card space-y-4" aria-labelledby="reading-h">
        <h2 id="reading-h" className="text-xl font-bold">Reading and motion</h2>
        <label className="flex items-start gap-3">
          <input type="checkbox" className="mt-1 h-5 w-5" checked={me.useDyslexiaFont} onChange={toggle('useDyslexiaFont')} />
          <span>
            <strong>Dyslexia-friendly font</strong>
            <span className="block text-sm text-muted">Uses OpenDyslexic with a little more space between letters and words.</span>
          </span>
        </label>
        <label className="flex items-start gap-3">
          <input type="checkbox" className="mt-1 h-5 w-5" checked={me.reducedMotion} onChange={toggle('reducedMotion')} />
          <span>
            <strong>Reduce motion</strong>
            <span className="block text-sm text-muted">Turns off pop-up animations.</span>
          </span>
        </label>
        <ErrorNote error={prefs.error} />
      </section>
    </div>
  );
}
