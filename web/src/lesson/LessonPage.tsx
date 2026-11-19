import { useQueryClient } from '@tanstack/react-query';
import { lazy, Suspense, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { api, ApiError } from '../api/client';
import { keys, useLesson, useMe } from '../api/queries';
import type { Files, Lesson, ProgressOutcome, RevealedHint, SubmissionResponse, TestResult, TutorMood } from '../api/types';
import { Markdown } from '../components/Markdown';
import { ErrorNote, Spinner } from '../components/ui';
import { CodeEditor } from './CodeEditor';
import { CSharpOutput } from './CSharpOutput';
import { LivePreview, type LivePreviewHandle } from './LivePreview';
import { preloadCSharp } from '../runner/csharp';
import { monacoTypeChecker } from './typeCheckMonaco';
import { CelebrationToast, HintLadder, QuizCard, TestResultList, useHeartbeat } from './parts';
import { TutorPanel } from '../tutor/TutorPanel';
import { useQuietMode } from '../tutor/quietMode';
import { useChallengeTutor } from '../tutor/tutorEvents';

// Blockly is big, so it only downloads for lessons that offer blocks.
const BlocksEditor = lazy(() => import('./blocks/BlocksEditor'));

type Step = 'learn' | 'example' | 'challenge' | 'quiz' | 'done';
type EditMode = 'blocks' | 'text';

/**
 * A submission made in blocks mode also carries the workspace as this hidden file, so the learner gets
 * their blocks back next time. The preview ignores it (it isn't linked from index.html) and the editor
 * never shows dotfiles.
 */
const BLOCKS_FILE = '.blocks.json';

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

  // Mark the learn step as seen once. The ref stops React's dev-mode double effects (StrictMode) from sending two
  // POSTs at the same moment; the server handles that race anyway, but EF logs the losing insert as an error.
  const viewedSentFor = useRef<number | null>(null);
  useEffect(() => {
    if (lesson.progress.learnViewed || viewedSentFor.current === lesson.id) return;
    viewedSentFor.current = lesson.id;
    void api.post(`/api/lessons/${lesson.id}/viewed`).catch(() => undefined);
  }, [lesson.id, lesson.progress.learnViewed]);

  // C# runs on .NET in the browser, a few MB the first time: start fetching it while the learner reads.
  const isCSharp = lesson.challenge?.kind === 'CSharp';
  useEffect(() => {
    if (isCSharp) preloadCSharp();
  }, [isCSharp]);

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
        // The tutor's intro sits beside the lesson on wide screens and above it on phones.
        <div className={lesson.tutor.intro.length ? 'grid grid-cols-1 items-start gap-6 lg:grid-cols-[minmax(0,1fr)_20rem]' : undefined}>
          <TutorPanel
            character={lesson.tutor.character}
            label="Before you start"
            lines={lesson.tutor.intro}
            className="lg:sticky lg:top-4 lg:order-2"
          />
          <section className="card space-y-6 lg:order-1">
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
        </div>
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
          {lesson.tutor.recap.length > 0
            ? <TutorPanel character={lesson.tutor.character} label="What you learned" lines={lesson.tutor.recap} className="mx-auto max-w-2xl text-left" />
            : <p>You learned something new today. That's how every developer started.</p>}
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
  // C# examples are a whole Program.cs; the Output panel runs them itself (autoRun).
  const isCSharp = lesson.challenge?.kind === 'CSharp';
  const exampleFiles = useMemo(() => ({ 'Program.cs': lesson.exampleCode ?? '' }), [lesson.exampleCode]);
  useEffect(() => {
    if (lesson.exampleCode && !isCSharp) void preview.current?.run({ 'index.html': lesson.exampleCode }, []);
  }, [lesson.exampleCode, isCSharp]);

  return (
    <section className="card space-y-4">
      <h2 ref={headingRef} tabIndex={-1} className="text-2xl font-bold">Example</h2>
      <Markdown>{lesson.exampleMarkdown}</Markdown>
      {lesson.exampleCode && (
        // C# output is a few lines of text, so the code gets more of the width than a web preview allows.
        <div className={`grid gap-4 ${isCSharp ? 'lg:grid-cols-[3fr_2fr]' : 'lg:grid-cols-2'}`}>
          <div className="min-w-0">
            <p className="mb-1 text-sm font-bold">The code</p>
            {/* Long lines wrap instead of scrolling sideways: kids miss code hidden past the edge. */}
            <pre tabIndex={0} className="whitespace-pre-wrap rounded-card bg-[#1b2128] p-4 font-mono text-sm text-[#e8edf2] [overflow-wrap:anywhere]"><code>{lesson.exampleCode}</code></pre>
          </div>
          <div className="h-72">
            {isCSharp
              ? <CSharpOutput files={exampleFiles} autoRun title="What it prints" />
              : <LivePreview ref={preview} title="Example preview" />}
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
  const isCSharp = challenge.kind === 'CSharp';
  const draftKey = `cg:draft:${challenge.id}`;
  const preview = useRef<LivePreviewHandle>(null);
  const [files, setFiles] = useState<Files>(() => readDraft(draftKey) ?? challenge.starterFiles);
  const [results, setResults] = useState<TestResult[] | null>(null);
  const [checking, setChecking] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [solution, setSolution] = useState<Files | null>(null);
  const checkingRef = useRef(false); // a live re-render must not cancel a check in progress

  // The tutor: event lines (first fail, stuck offer, pass) and the mood of the latest hint.
  const [quiet] = useQuietMode();
  const tutor = useChallengeTutor(lesson.tutor, { quiet, alreadyPassed: passed });
  const [hintMood, setHintMood] = useState<TutorMood | undefined>(undefined);
  const [hintRequest, setHintRequest] = useState(0);
  const bestPassing = useRef(0);
  const { typed } = tutor;
  const onEdit = useCallback((f: Files) => {
    typed();
    setFiles(f);
  }, [typed]);

  // Blocks mode (Explorer JS lessons). The blocks write script.js; Text mode edits it directly.
  // Workspace state and the chosen mode are kept for this tab session only, like drafts.
  const blocks = challenge.blocks;
  const blocksKey = `cg:blocks:${challenge.id}`;
  const modeKey = `cg:mode:${challenge.id}`;
  const [mode, setModeState] = useState<EditMode>(() =>
    blocks && sessionStorage.getItem(modeKey) !== 'text' ? 'blocks' : 'text',
  );
  const blocksState = useRef<object | null>(readJson<object>(blocksKey) ?? blocks?.starter ?? null);
  const lastGenerated = useRef<string | null>(null); // script.js as the blocks last made it
  const [blocksEpoch, setBlocksEpoch] = useState(0); // bump to remount the workspace (Start over)

  const setMode = useCallback((m: EditMode) => {
    setModeState(m);
    sessionStorage.setItem(modeKey, m);
  }, [modeKey]);

  const onBlocksChange = useCallback((state: object, script: string) => {
    typed();
    blocksState.current = state;
    lastGenerated.current = script;
    sessionStorage.setItem(blocksKey, JSON.stringify(state));
    setFiles((f) => (f['script.js'] === script ? f : { ...f, 'script.js': script }));
  }, [blocksKey, typed]);

  function switchToBlocks() {
    const script = files['script.js'];
    const typedChanges = script !== lastGenerated.current && script !== challenge.starterFiles['script.js'];
    if (typedChanges && !window.confirm(
      "Blocks can't read code you typed. If you switch back, script.js becomes the code your blocks make, and your typed changes are lost. Switch to blocks?",
    )) return;
    setMode('blocks');
  }

  // Restore the last submitted code if there's no draft in this tab. A submission made with blocks
  // reopens in Blocks with the learner's workspace; one typed as text reopens in Text.
  useEffect(() => {
    if (readDraft(draftKey)) return;
    api.get<{ files: Files } | undefined>(`/api/challenges/${challenge.id}/submissions/latest`)
      .then((latest) => {
        if (!latest?.files) return;
        const { [BLOCKS_FILE]: savedBlocks, ...code } = latest.files;
        setFiles(code);
        if (!blocks) return;
        const state = savedBlocks ? parseJson<object>(savedBlocks) : null;
        if (state) {
          blocksState.current = state;
          setBlocksEpoch((n) => n + 1); // remount the workspace with the saved blocks
          setMode('blocks');
        } else {
          setMode('text');
        }
      })
      .catch(() => undefined);
  }, [challenge.id, draftKey, blocks, setMode]);

  // Live preview: re-render 400 ms after typing stops (not C#: a console program runs when the learner
  // presses Run). Drafts are kept for this tab session only.
  useEffect(() => {
    const t = window.setTimeout(() => {
      if (!checkingRef.current && !isCSharp) void preview.current?.run(files, []);
      sessionStorage.setItem(draftKey, JSON.stringify(files));
    }, 400);
    return () => window.clearTimeout(t);
  }, [files, draftKey, isCSharp]);

  const testsById = useMemo(() => challenge.tests, [challenge.tests]);

  async function check() {
    checkingRef.current = true;
    setChecking(true);
    setError(null);
    try {
      const res = await preview.current?.run(files, testsById);
      if (!res) {
        setError(new ApiError(0, {
          title: isCSharp
            ? "Your code couldn't be checked this time. The Output panel says why."
            : "Your page didn't finish loading. Look for an error in the console under the preview.",
        }));
        return;
      }
      setResults(res);
      const passing = res.filter((r) => r.passed).length;
      if (passing > bestPassing.current) {
        bestPassing.current = passing;
        tutor.progressed();
      }
      const submittedFiles = mode === 'blocks' && blocksState.current
        ? { ...files, [BLOCKS_FILE]: JSON.stringify(blocksState.current) }
        : files;
      const submitted = await api.post<SubmissionResponse>(`/api/challenges/${challenge.id}/submissions`, { files: submittedFiles, results: res });
      onOutcome(submitted.outcome);
      tutor.checked(submitted.passed);
      if (submitted.passed) onPassed();
    } catch (e) {
      setError(e);
    } finally {
      checkingRef.current = false;
      setChecking(false);
    }
  }

  const { dismiss, helped } = tutor;
  const onSolution = useCallback((f: Files) => {
    setSolution(f);
    helped();
  }, [helped]);
  const onHint = useCallback((h: RevealedHint) => {
    setHintMood(h.mood);
    dismiss();
    helped();
  }, [dismiss, helped]);

  return (
    <section className="space-y-4">
      <div className="card space-y-3">
        <h2 ref={headingRef} tabIndex={-1} className="text-2xl font-bold">Your challenge</h2>
        <Markdown>{challenge.instructionsMarkdown}</Markdown>
      </div>

      {blocks && (
        <div className="flex flex-wrap items-center gap-3">
          <div role="group" aria-label="Code with" className="inline-flex overflow-hidden rounded-card border border-line">
            <button
              type="button"
              aria-pressed={mode === 'blocks'}
              onClick={() => mode !== 'blocks' && switchToBlocks()}
              className={`px-4 py-1.5 font-bold ${mode === 'blocks' ? 'bg-brand text-brand-ink' : 'bg-surface text-muted hover:bg-surface-2'}`}
            >
              <span aria-hidden="true">🧩 </span>Blocks
            </button>
            <button
              type="button"
              aria-pressed={mode === 'text'}
              onClick={() => setMode('text')}
              className={`px-4 py-1.5 font-bold ${mode === 'text' ? 'bg-brand text-brand-ink' : 'bg-surface text-muted hover:bg-surface-2'}`}
            >
              <span aria-hidden="true">⌨️ </span>Text
            </button>
          </div>
          <p className="text-sm text-muted">
            {mode === 'blocks'
              ? 'Snap blocks together. The JavaScript they make shows up underneath. Ready for real code? Switch to Text.'
              : "You're writing real JavaScript! You can still switch back to blocks."}
          </p>
        </div>
      )}

      <div className="grid gap-4 lg:grid-cols-2">
        {mode === 'blocks' && blocks ? (
          <div className="flex h-[38rem] flex-col overflow-hidden rounded-card border border-line bg-surface">
            <div className="min-h-0 flex-1">
              <Suspense fallback={<Spinner label="Loading blocks" />}>
                <BlocksEditor
                  key={blocksEpoch}
                  toolbox={blocks.toolbox}
                  initialState={blocksState.current}
                  onChange={onBlocksChange}
                  dark={dark}
                />
              </Suspense>
            </div>
            <section aria-labelledby="blocks-code-heading" className="flex h-44 shrink-0 flex-col border-t border-line">
              <h3 id="blocks-code-heading" className="bg-surface-2 px-3 py-1 text-sm font-bold">
                Your blocks as JavaScript <span className="font-normal text-muted">(script.js)</span>
              </h3>
              <pre tabIndex={0} className="min-h-0 flex-1 overflow-auto whitespace-pre-wrap bg-[#1b2128] p-3 font-mono text-sm leading-snug text-[#e8edf2]">
                <code>{files['script.js']}</code>
              </pre>
            </section>
          </div>
        ) : (
          <div className={blocks ? 'h-[38rem]' : 'h-[28rem]'}>
            <CodeEditor files={files} onChange={onEdit} dark={dark} />
          </div>
        )}
        <div className={blocks ? 'h-[38rem]' : 'h-[28rem]'}>
          {isCSharp
            ? <CSharpOutput ref={preview} files={files} />
            : <LivePreview ref={preview} title="Your page preview" typeChecker={monacoTypeChecker} />}
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
              sessionStorage.removeItem(blocksKey);
              setFiles(challenge.starterFiles);
              setResults(null);
              blocksState.current = blocks?.starter ?? null;
              lastGenerated.current = null;
              setBlocksEpoch((n) => n + 1);
            }
          }}
        >
          Start over
        </button>
        {passed && <button type="button" className="btn-primary" onClick={onNext}>{lesson.quiz ? 'On to the quiz →' : 'Finish lesson →'}</button>}
      </div>

      <ErrorNote error={error} />
      <TestResultList challenge={challenge} results={results} />

      <TutorPanel
        character={lesson.tutor.character}
        label="Here to help"
        lines={tutor.current ? [tutor.current.line] : []}
        mood={tutor.current ? undefined : hintMood}
        live
        actions={tutor.current?.event === 'stuckOffer' ? (
          <>
            <button type="button" className="btn-primary" onClick={() => setHintRequest((n) => n + 1)}>Yes, a hint please</button>
            <button type="button" className="btn-secondary" onClick={tutor.dismiss}>No thanks</button>
          </>
        ) : tutor.current ? (
          <button type="button" className="btn-secondary" onClick={tutor.dismiss}>OK</button>
        ) : undefined}
      >
        <HintLadder challenge={challenge} onSolution={onSolution} onHint={onHint} hintRequest={hintRequest} embedded />
      </TutorPanel>

      {solution && (
        <section aria-labelledby="solution-heading" className="card space-y-2">
          <h3 id="solution-heading" className="text-lg font-bold">One possible solution</h3>
          <p className="text-muted">
            {mode === 'blocks'
              ? 'This is the solution as JavaScript. Can you build the same thing with blocks? Or switch to Text and type it.'
              : 'Read it, compare it with yours, then type the changes yourself.'}
          </p>
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
  return readJson<Files>(key);
}

function readJson<T>(key: string): T | null {
  const raw = sessionStorage.getItem(key);
  return raw ? parseJson<T>(raw) : null;
}

function parseJson<T>(raw: string): T | null {
  try {
    return JSON.parse(raw) as T;
  } catch {
    return null;
  }
}
