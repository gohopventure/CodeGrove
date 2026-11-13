import { useCallback, useEffect, useRef, useState } from 'react';
import { api, ApiError } from '../api/client';
import type { Challenge, Files, HintResponse, ProgressOutcome, Quiz, QuizAttemptResponse, RevealedHint, SolutionResponse, TestResult } from '../api/types';
import { Markdown } from '../components/Markdown';
import { ErrorNote } from '../components/ui';

// ---- Test results ---------------------------------------------------------------------------------

export function TestResultList({ challenge, results }: { challenge: Challenge; results: TestResult[] | null }) {
  if (!results) return null;
  const passedCount = results.filter((r) => r.passed).length;
  const allPassed = passedCount === challenge.tests.length;

  return (
    <section aria-labelledby="results-heading" className="card space-y-3">
      <h3 id="results-heading" className="text-lg font-bold" aria-live="polite">
        {allPassed ? 'All checks passed. Nice work!' : `${passedCount} of ${challenge.tests.length} checks passed. Keep going!`}
      </h3>
      <ul className="space-y-2">
        {challenge.tests.map((t) => {
          const r = results.find((x) => x.testId === t.id);
          const ok = r?.passed === true;
          return (
            <li key={t.id} className="flex gap-3">
              <span aria-hidden="true" className={`mt-0.5 font-bold ${ok ? 'text-ok' : 'text-bad'}`}>{ok ? '✓' : '✕'}</span>
              <span>
                <span className="sr-only">{ok ? 'Passed: ' : 'Not yet: '}</span>
                <span className={ok ? '' : 'font-bold'}>{t.label}</span>
                {!ok && <span className="block text-sm text-muted">{r?.message ?? t.failMessage}</span>}
              </span>
            </li>
          );
        })}
      </ul>
    </section>
  );
}

// ---- Hints ----------------------------------------------------------------------------------------

const HINT_NAMES: Record<number, string> = { 1: 'Give me a nudge', 2: 'More help, please', 3: 'Almost there…' };

/**
 * The hint ladder: a guiding question, then a pointer, then nearly the answer. Hints unlock one at a time and never
 * cost XP. The full solution is not a hint: it's a separate step after all three, and it asks "are you sure?" first,
 * because trying is how learning sticks.
 */
export function HintLadder({
  challenge,
  onSolution,
  onHint,
  hintRequest = 0,
  embedded = false,
}: {
  challenge: Challenge;
  onSolution: (files: Files) => void;
  /** Called with each newly revealed hint, so the tutor's face can match its mood. */
  onHint?: (hint: RevealedHint) => void;
  /** Increase to reveal the next hint from outside, e.g. when the learner says yes to the tutor's offer. */
  hintRequest?: number;
  /** Inside the tutor panel: no card of its own, and the panel provides the heading. */
  embedded?: boolean;
}) {
  const [revealed, setRevealed] = useState(challenge.revealedHints);
  const [confirming, setConfirming] = useState(false);
  const [hasSolution, setHasSolution] = useState(challenge.solutionFiles !== null);
  const [error, setError] = useState<unknown>(null);
  const next = challenge.hintLevels.find((l) => !revealed.some((h) => h.level === l));

  useEffect(() => {
    if (challenge.solutionFiles) onSolution(challenge.solutionFiles);
  }, [challenge.solutionFiles, onSolution]);

  const reveal = useCallback(async (level: number) => {
    setError(null);
    try {
      const hint = await api.post<HintResponse>(`/api/challenges/${challenge.id}/hints/${level}`);
      setRevealed((r) => (r.some((h) => h.level === hint.level) ? r : [...r, hint]));
      onHint?.(hint);
    } catch (e) {
      setError(e);
    }
  }, [challenge.id, onHint]);

  async function showSolution() {
    setError(null);
    try {
      const res = await api.post<SolutionResponse>(`/api/challenges/${challenge.id}/solution`);
      setHasSolution(true);
      onSolution(res.solutionFiles);
    } catch (e) {
      setError(e);
    } finally {
      setConfirming(false);
    }
  }

  // A "yes" to the tutor's offer opens the next hint (never the solution).
  const handled = useRef(hintRequest);
  useEffect(() => {
    if (hintRequest === handled.current) return;
    handled.current = hintRequest;
    if (next !== undefined) void reveal(next);
  }, [hintRequest, next, reveal]);

  return (
    <section aria-labelledby="hints-heading" className={embedded ? 'space-y-3' : 'card space-y-3'}>
      <h3 id="hints-heading" className={embedded ? 'font-bold' : 'text-lg font-bold'}>Stuck? Hints are free.</h3>
      <ol className="space-y-3" aria-live="polite">
        {revealed.map((h) => (
          <li key={h.level} className="rounded-card bg-surface-2 p-3">
            <p className="text-sm font-bold text-muted">Hint {h.level}</p>
            <Markdown>{h.markdown}</Markdown>
          </li>
        ))}
      </ol>
      <ErrorNote error={error} />
      {next !== undefined && (
        <button type="button" className="btn-secondary" onClick={() => void reveal(next)}>
          {HINT_NAMES[next] ?? `Hint ${next}`}
        </button>
      )}
      {next === undefined && !hasSolution && !confirming && (
        <button type="button" className="btn-secondary" onClick={() => setConfirming(true)}>Show me the solution</button>
      )}
      {confirming && (
        <div role="alertdialog" aria-labelledby="sol-q" className="space-y-2 rounded-card border border-accent p-3">
          <p id="sol-q" className="font-bold">Did hint 3 get you close? Seeing the answer is OK, but one more try helps your brain remember.</p>
          <div className="flex gap-2">
            <button type="button" className="btn-primary" onClick={() => void showSolution()}>Show the solution</button>
            <button type="button" className="btn-secondary" onClick={() => setConfirming(false)}>I'll keep trying</button>
          </div>
        </div>
      )}
    </section>
  );
}

// ---- Quiz -----------------------------------------------------------------------------------------

export function QuizCard({ quiz, onPassed }: { quiz: Quiz; onPassed: (outcome: ProgressOutcome) => void }) {
  const [index, setIndex] = useState(0);
  const [answers, setAnswers] = useState<Record<number, number>>({});
  const [result, setResult] = useState<QuizAttemptResponse | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<unknown>(null);

  const question = quiz.questions[index];
  const isLast = index === quiz.questions.length - 1;

  async function submit() {
    setSubmitting(true);
    setError(null);
    try {
      const res = await api.post<QuizAttemptResponse>(`/api/quizzes/${quiz.id}/attempts`, { answers });
      setResult(res);
      if (res.passed) onPassed(res.outcome);
    } catch (e) {
      setError(e);
    } finally {
      setSubmitting(false);
    }
  }

  if (result) {
    return (
      <section aria-labelledby="quiz-result" className="card space-y-4">
        <h2 id="quiz-result" className="text-2xl font-bold" aria-live="polite">
          {result.passed ? `You scored ${result.scorePercent}%. Quiz passed!` : `You scored ${result.scorePercent}%. You need ${result.passPercent}% to pass.`}
        </h2>
        <ol className="space-y-3">
          {quiz.questions.map((q, i) => {
            const r = result.results.find((x) => x.questionId === q.id);
            return (
              <li key={q.id} className="rounded-card bg-surface-2 p-3">
                <p className="font-bold">
                  <span aria-hidden="true" className={r?.correct ? 'text-ok' : 'text-bad'}>{r?.correct ? '✓ ' : '✕ '}</span>
                  <span className="sr-only">{r?.correct ? 'Correct: ' : 'Not quite: '}</span>
                  {i + 1}. {q.prompt}
                </p>
                <p className="text-muted">{r?.explanation}</p>
              </li>
            );
          })}
        </ol>
        {!result.passed && (
          <button type="button" className="btn-primary" onClick={() => { setResult(null); setIndex(0); setAnswers({}); }}>
            Try the quiz again
          </button>
        )}
      </section>
    );
  }

  if (!question) return null;
  const legendId = `q-${question.id}`;

  return (
    <section className="card space-y-4" aria-label="Quiz">
      <p className="text-sm font-bold text-muted">Question {index + 1} of {quiz.questions.length}</p>
      <fieldset aria-labelledby={legendId} className="space-y-2">
        <legend id={legendId} className="mb-2 text-xl font-bold">{question.prompt}</legend>
        {question.options.map((o) => (
          <label key={o.id} className={`flex cursor-pointer items-center gap-3 rounded-card border p-3 ${answers[question.id] === o.id ? 'border-brand bg-surface-2' : 'border-line'}`}>
            <input
              type="radio"
              name={`question-${question.id}`}
              value={o.id}
              checked={answers[question.id] === o.id}
              onChange={() => setAnswers((a) => ({ ...a, [question.id]: o.id }))}
              className="h-5 w-5 accent-[var(--brand)]"
            />
            <span className="font-mono">{o.text}</span>
          </label>
        ))}
      </fieldset>
      <ErrorNote error={error} />
      <div className="flex gap-2">
        {index > 0 && <button type="button" className="btn-secondary" onClick={() => setIndex(index - 1)}>Back</button>}
        {isLast ? (
          <button type="button" className="btn-primary" disabled={answers[question.id] === undefined || submitting} onClick={submit}>
            {submitting ? 'Checking…' : 'Check my answers'}
          </button>
        ) : (
          <button type="button" className="btn-primary" disabled={answers[question.id] === undefined} onClick={() => setIndex(index + 1)}>
            Next question
          </button>
        )}
      </div>
    </section>
  );
}

// ---- Celebration ----------------------------------------------------------------------------------

/** XP and badge pop-up. Announced politely to screen readers; no sound; animation respects reduced motion. */
export function CelebrationToast({ outcome, onClose }: { outcome: ProgressOutcome | null; onClose: () => void }) {
  useEffect(() => {
    if (!outcome) return;
    const t = window.setTimeout(onClose, 8000);
    return () => window.clearTimeout(t);
  }, [outcome, onClose]);

  if (!outcome || (outcome.xpAwarded === 0 && outcome.newBadges.length === 0)) return null;

  return (
    <div role="status" className="animate-pop fixed bottom-6 right-6 z-40 max-w-sm card space-y-2 shadow-xl">
      {outcome.xpAwarded > 0 && <p className="text-xl font-bold">+{outcome.xpAwarded} XP <span className="text-base text-muted">· Level {outcome.level}</span></p>}
      {outcome.newBadges.map((b) => (
        <p key={b.code} className="flex items-center gap-3">
          <img src={b.iconUrl} alt="" width={40} height={40} />
          <span><strong>New badge: {b.name}</strong><br /><span className="text-sm text-muted">{b.description}</span></span>
        </p>
      ))}
      {outcome.certificateCode && <p className="font-bold">You earned a certificate! Find it on your home page.</p>}
      <button type="button" className="btn-secondary w-full" onClick={onClose}>Close</button>
    </div>
  );
}

// ---- Heartbeat ------------------------------------------------------------------------------------

/**
 * Reports active time every 30 s while the lesson tab is visible. Returns true once the parent's
 * daily limit is reached (the API answers 423).
 */
export function useHeartbeat(): boolean {
  const [limitReached, setLimitReached] = useState(false);

  useEffect(() => {
    const beat = async () => {
      if (document.visibilityState !== 'visible') return;
      try {
        const d = new Date();
        const localDate = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
        await api.post('/api/activity/heartbeat', { localDate });
      } catch (e) {
        if (e instanceof ApiError && e.status === 423) setLimitReached(true);
      }
    };
    const id = window.setInterval(beat, 30_000);
    return () => window.clearInterval(id);
  }, []);

  return limitReached;
}
