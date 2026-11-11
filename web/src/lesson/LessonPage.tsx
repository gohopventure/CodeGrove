import { useQueryClient } from '@tanstack/react-query';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { api, ApiError } from '../api/client';
import { keys, useLesson, useMe } from '../api/queries';
import type { Files, Lesson, ProgressOutcome, SubmissionResponse, TestResult } from '../api/types';
import { Markdown } from '../components/Markdown';
import { ErrorNote, Spinner } from '../components/ui';
import { CodeEditor } from './CodeEditor';
import { LivePreview, type LivePreviewHandle } from './LivePreview';
import { CelebrationToast, HintLadder, QuizCard, TestResultList, useHeartbeat } from './parts';

type Step = 'learn' | 'example' | 'challenge' | 'quiz' | 'done';

const STEPS: { id: Step; label: string }[] = [
  { id: 'learn', label: 'Learn' },
  { id: 'example', label: 'Example' },
  { id: 'challenge', label: 'Challenge' },
  { id: 'quiz', label: 'Quiz' },
];

/** Lesson player: Learn → Example → Challenge → Quiz. Default export for React.lazy. */
export default function LessonPage() {
  const id = Number(useParams().lessonId);
  const { data: lesson, error, isLoading } = useLesson(id);

  if (isLoading) return <Spinner label="Loading your lesson" />;
  if (error || !lesson) {
    return (
      <div className="space-y-4">
        <ErrorNote error={error ?? new Error()} />
        <Link to="/learn" className="link">Back to your home page</Link>
      </div>
    );
  }
  // Keyed so moving to the next lesson resets every piece of state.
  return <LessonPlayer key={lesson.id} lesson={lesson} />;
}

function firstStep(lesson: Lesson): Step {
  if (lesson.progress.completed) return 'done';
  if (lesson.progress.challengePassed) return 'quiz';
  return 'learn';
}

function LessonPlayer({ lesson }: { lesson: Lesson }) {
  const qc = useQueryClient();
  const { data: me } = useMe();
  const dark = me?.ageBand === 'Builder';
  const [step, setStep] = useState<Step>(() => firstStep(lesson));
  const [toast, setToast] = useState<ProgressOutcome | null>(null);
  const [challengePassed, setChallengePassed] = useState(lesson.progress.challengePassed);
  const limitReached = useHeartbeat();
  const headingRef = useRef<HTMLHeadingElement>(null);

  // Mark the learn step as seen once.
  useEffect(() => {
    if (!lesson.progress.learnViewed) void api.post(`/api/lessons/${lesson.id}/viewed`).catch(() => undefined);
  }, [lesson.id, lesson.progress.learnViewed]);

  // Move focus to the step heading on step change, so keyboard and screen-reader users land in the new content.
  useEffect(() => headingRef.current?.focus(), [step]);

  const onOutcome = useCallback((o: ProgressOutcome) => {
    setToast(o);
    void qc.invalidateQueries({ queryKey: keys.dashboard });
    void qc.invalidateQueries({ queryKey: keys.course(lesson.courseSlug) });
  }, [qc, lesson.courseSlug]);

  if (limitReached) {
    return (
      <div className="card mx-auto max-w-xl space-y-4 text-center">
        <h1 className="text-3xl font-bold">That's all for today!</h1>
        <p>You've reached the time your grown-up set for today. Your work is saved, so you can pick up right here tomorrow.</p>
        <Link to="/learn" className="btn-primary">Go to my home page</Link>
      </div>
    );
  }

  const stepIndex = STEPS.findIndex((s) => s.id === step);

  return (
    <div className="space-y-6">
      <div>
        <Link to={`/courses/${lesson.courseSlug}`} className="link text-sm">← {lesson.courseTitle}</Link>
        <h1 className="mt-1 text-3xl font-bold">{lesson.title}</h1>
        <p className="text-muted">About {lesson.estimatedMinutes} minutes</p>
      </div>

      <nav aria-label="Lesson steps">
        <ol className="flex flex-wrap gap-2">
          {STEPS.map((s, i) => {
            const locked = s.id === 'quiz' && !challengePassed && !!lesson.challenge;
            const current = s.id === step;
            const done = i < stepIndex || step === 'done';
            return (
              <li key={s.id}>
                <button
                  type="button"
                  disabled={locked}
                  aria-current={current ? 'step' : undefined}
                  onClick={() => setStep(s.id)}
                  className={`rounded-full border px-4 py-1.5 font-bold ${current ? 'border-brand bg-brand text-brand-ink' : 'border-line bg-surface'} disabled:opacity-50`}
                >
                  <span aria-hidden="true">{done ? '✓ ' : `${i + 1}. `}</span>
                  {s.label}
                  {done && <span className="sr-only"> (done)</span>}
                  {locked && <span className="sr-only"> (unlocks after the challenge)</span>}
                </button>
              </li>
            );
          })}
        </ol>
      </nav>

      {step === 'learn' && (
        <section className="card space-y-6">
          <h2 ref={headingRef} tabIndex={-1} className="sr-only">Learn</h2>
          {lesson.videoUrl && (
            <video controls preload="metadata" className="w-full rounded-card">
              <source src={lesson.videoUrl} />
              {lesson.captionsUrl && <track kind="captions" src={lesson.captionsUrl} srcLang="en" label="English" default />}
            </video>
          )}
          <Markdown>{lesson.learnMarkdown}</Markdown>
          <button type="button" className="btn-primary" onClick={() => setStep('example')}>See an example →</button>
        </section>
      )}

      {step === 'example' && (
        <ExampleStep lesson={lesson} headingRef={headingRef} onNext={() => setStep(lesson.challenge ? 'challenge' : 'quiz')} />
      )}

      {step === 'challenge' && lesson.challenge && (
        <ChallengeStep
          lesson={lesson}
          dark={dark}
          headingRef={headingRef}
          onOutcome={onOutcome}
          onPassed={() => setChallengePassed(true)}
          onNext={() => setStep(lesson.quiz ? 'quiz' : 'done')}
          passed={challengePassed}
        />
      )}

      {step === 'quiz' && lesson.quiz && (
        <div className="space-y-4">
          <h2 ref={headingRef} tabIndex={-1} className="text-2xl font-bold">Quick quiz</h2>
          <QuizCard
            quiz={lesson.quiz}
            onPassed={(o) => {
              onOutcome(o);
              void qc.invalidateQueries({ queryKey: keys.lesson(lesson.id) });
              setStep('done');
            }}
          />
        </div>
      )}

      {step === 'done' && (
        <section className="card space-y-4 text-center">
          <h2 ref={headingRef} tabIndex={-1} className="text-3xl font-bold">Lesson complete!</h2>
          <p>You learned something new today. That's how every developer started.</p>
          <div className="flex flex-wrap justify-center gap-3">
            {lesson.nextLessonId ? (
              <Link to={`/lessons/${lesson.nextLessonId}`} className="btn-primary">Next lesson →</Link>
            ) : (
              <Link to={`/courses/${lesson.courseSlug}`} className="btn-primary">Back to the course</Link>
            )}
            <button type="button" className="btn-secondary" onClick={() => setStep('challenge')}>Play with my code</button>
          </div>
        </section>
      )}

      <CelebrationToast outcome={toast} onClose={() => setToast(null)} />
    </div>
  );
}

function ExampleStep({ lesson, headingRef, onNext }: { lesson: Lesson; headingRef: React.RefObject<HTMLHeadingElement | null>; onNext: () => void }) {
  const preview = useRef<LivePreviewHandle>(null);
  useEffect(() => {
    if (lesson.exampleCode) void preview.current?.run({ 'index.html': lesson.exampleCode }, []);
  }, [lesson.exampleCode]);

  return (
    <section className="card space-y-4">
      <h2 ref={headingRef} tabIndex={-1} className="text-2xl font-bold">Example</h2>
      <Markdown>{lesson.exampleMarkdown}</Markdown>
      {lesson.exampleCode && (
        <div className="grid gap-4 lg:grid-cols-2">
          <div>
            <p className="mb-1 text-sm font-bold">The code</p>
            <pre tabIndex={0} className="overflow-x-auto rounded-card bg-[#1b2128] p-4 font-mono text-sm text-[#e8edf2]"><code>{lesson.exampleCode}</code></pre>
          </div>
          <div className="h-72">
            <LivePreview ref={preview} title="Example preview" />
          </div>
        </div>
      )}
      <button type="button" className="btn-primary" onClick={onNext}>Try it yourself →</button>
    </section>
  );
}

function ChallengeStep({
  lesson, dark, headingRef, onOutcome, onPassed, onNext, passed,
}: {
  lesson: Lesson;
  dark: boolean;
  headingRef: React.RefObject<HTMLHeadingElement | null>;
  onOutcome: (o: ProgressOutcome) => void;
  onPassed: () => void;
  onNext: () => void;
  passed: boolean;
}) {
  const challenge = lesson.challenge!;
  const draftKey = `cg:draft:${challenge.id}`;
  const preview = useRef<LivePreviewHandle>(null);
  const [files, setFiles] = useState<Files>(() => readDraft(draftKey) ?? challenge.starterFiles);
  const [results, setResults] = useState<TestResult[] | null>(null);
  const [checking, setChecking] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [solution, setSolution] = useState<Files | null>(null);
  const checkingRef = useRef(false); // a live re-render must not cancel a check in progress

  // Restore the last submitted code if there's no draft in this tab.
  useEffect(() => {
    if (readDraft(draftKey)) return;
    api.get<{ files: Files } | undefined>(`/api/challenges/${challenge.id}/submissions/latest`)
      .then((latest) => latest?.files && setFiles(latest.files))
      .catch(() => undefined);
  }, [challenge.id, draftKey]);

  // Live preview: re-render 400 ms after typing stops. Drafts are kept for this tab session only.
  useEffect(() => {
    const t = window.setTimeout(() => {
      if (!checkingRef.current) void preview.current?.run(files, []);
      sessionStorage.setItem(draftKey, JSON.stringify(files));
    }, 400);
    return () => window.clearTimeout(t);
  }, [files, draftKey]);

  const testsById = useMemo(() => challenge.tests, [challenge.tests]);

  async function check() {
    checkingRef.current = true;
    setChecking(true);
    setError(null);
    try {
      const res = await preview.current?.run(files, testsById);
      if (!res) {
        setError(new ApiError(0, { title: "Your page didn't finish loading. Look for an error in the console under the preview." }));
        return;
      }
      setResults(res);
      const submitted = await api.post<SubmissionResponse>(`/api/challenges/${challenge.id}/submissions`, { files, results: res });
      onOutcome(submitted.outcome);
      if (submitted.passed) onPassed();
    } catch (e) {
      setError(e);
    } finally {
      checkingRef.current = false;
      setChecking(false);
    }
  }

  const onSolution = useCallback((f: Files) => setSolution(f), []);

  return (
    <section className="space-y-4">
      <div className="card space-y-3">
        <h2 ref={headingRef} tabIndex={-1} className="text-2xl font-bold">Your challenge</h2>
        <Markdown>{challenge.instructionsMarkdown}</Markdown>
      </div>

      <div className="grid gap-4 lg:grid-cols-2">
        <div className="h-[28rem]">
          <CodeEditor files={files} onChange={setFiles} dark={dark} />
        </div>
        <div className="h-[28rem]">
          <LivePreview ref={preview} title="Your page preview" />
        </div>
      </div>

      <div className="flex flex-wrap gap-3">
        <button type="button" className="btn-primary" onClick={check} disabled={checking}>
          {checking ? 'Checking…' : 'Check my code'}
        </button>
        <button
          type="button"
          className="btn-secondary"
          onClick={() => {
            if (window.confirm('Start over with the original code? Your changes will be lost.')) {
              sessionStorage.removeItem(draftKey);
              setFiles(challenge.starterFiles);
              setResults(null);
            }
          }}
        >
          Start over
        </button>
        {passed && <button type="button" className="btn-primary" onClick={onNext}>{lesson.quiz ? 'On to the quiz →' : 'Finish lesson →'}</button>}
      </div>

      <ErrorNote error={error} />
      <TestResultList challenge={challenge} results={results} />

      <HintLadder challenge={challenge} onSolution={onSolution} />

      {solution && (
        <section aria-labelledby="solution-heading" className="card space-y-2">
          <h3 id="solution-heading" className="text-lg font-bold">One possible solution</h3>
          <p className="text-muted">Read it, compare it with yours, then type the changes yourself.</p>
          {Object.entries(solution).map(([name, code]) => (
            <div key={name}>
              <p className="font-mono text-sm font-bold">{name}</p>
              <pre tabIndex={0} className="overflow-x-auto rounded-card bg-[#1b2128] p-4 font-mono text-sm text-[#e8edf2]"><code>{code}</code></pre>
            </div>
          ))}
        </section>
      )}
    </section>
  );
}

function readDraft(key: string): Files | null {
  try {
    const raw = sessionStorage.getItem(key);
    return raw ? (JSON.parse(raw) as Files) : null;
  } catch {
    return null;
  }
}
