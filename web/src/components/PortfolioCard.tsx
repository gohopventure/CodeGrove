import { useId, useState, type FormEvent } from 'react';
import { Link } from 'react-router-dom';
import { api, ApiError } from '../api/client';
import { useRenamePortfolioEntry } from '../api/queries';
import type { PortfolioEntry, PortfolioFiles } from '../api/types';
import { downloadProject } from '../runner/exportProject';
import { ErrorNote } from './ui';

/**
 * One finished project: a picture of its page, its name and theme, and Download project (the same zip as the
 * button in the lesson, made from the passing code saved with the entry). The learner can rename it; a parent
 * sees the same card without the rename and the lesson link.
 */
export function PortfolioCard({ entry, editable, headingLevel = 2 }: {
  entry: PortfolioEntry;
  editable: boolean;
  /** 3 when the cards sit under a section heading (the parent's page). */
  headingLevel?: 2 | 3;
}) {
  const headingId = useId();
  const Heading = headingLevel === 3 ? 'h3' : 'h2';
  const [renaming, setRenaming] = useState(false);
  const [downloading, setDownloading] = useState(false);
  const [downloadError, setDownloadError] = useState<unknown>(null);

  async function download() {
    setDownloading(true);
    setDownloadError(null);
    try {
      const project = await api.get<PortfolioFiles>(`/api/portfolio/${encodeURIComponent(entry.id)}/files`);
      downloadProject(project.files, project.title);
    } catch (e) {
      setDownloadError(e);
    } finally {
      setDownloading(false);
    }
  }

  return (
    <article aria-labelledby={headingId} className="card flex flex-col gap-3">
      {entry.screenshotUrl ? (
        <img
          src={entry.screenshotUrl}
          alt={`The page of ${entry.title}`}
          width={1200}
          height={750}
          className="aspect-[16/10] h-auto w-full rounded border border-line bg-white object-cover object-top"
        />
      ) : (
        <div className="flex aspect-[16/10] w-full items-center justify-center rounded border border-dashed border-line bg-surface-2 p-4 text-center text-sm text-muted">
          {editable ? (
            <p>
              No picture yet. Open <Link className="link" to={`/lessons/${entry.lessonId}`}>{entry.lessonTitle}</Link> and
              press <strong>Check my code</strong> to take one.
            </p>
          ) : (
            <p>No picture yet.</p>
          )}
        </div>
      )}

      {renaming ? (
        <RenameForm entry={entry} onDone={() => setRenaming(false)} />
      ) : (
        <div className="flex items-start justify-between gap-2">
          <Heading id={headingId} className="text-xl font-bold">{entry.title}</Heading>
          {editable && (
            <button type="button" className="link shrink-0 text-sm" onClick={() => setRenaming(true)}>
              Rename<span className="sr-only"> {entry.title}</span>
            </button>
          )}
        </div>
      )}

      <p className="flex flex-wrap items-center gap-2 text-sm">
        <span className="rounded-full bg-surface-2 px-2 py-0.5 font-bold">
          <span className="sr-only">Theme: </span>{entry.theme}
        </span>
        <span className="text-muted">
          {entry.courseTitle} · finished {new Date(entry.updatedAt).toLocaleDateString()}
        </span>
      </p>

      <div className="mt-auto flex flex-wrap gap-2">
        <button type="button" className="btn-primary" onClick={() => void download()} disabled={downloading}>
          <span aria-hidden="true">⤓ </span>{downloading ? 'Getting it ready…' : 'Download project'}
          <span className="sr-only"> {entry.title}</span>
        </button>
        {editable && (
          <Link to={`/lessons/${entry.lessonId}`} className="btn-secondary">Open the lesson</Link>
        )}
      </div>
      {editable && (
        <p className="text-sm text-muted">
          The download runs on a real computer with <code>dotnet run</code> and <code>npm run dev</code>. Ask an adult
          before you install anything.
        </p>
      )}
      <ErrorNote error={downloadError} />
    </article>
  );
}

function RenameForm({ entry, onDone }: { entry: PortfolioEntry; onDone: () => void }) {
  const id = useId();
  const [title, setTitle] = useState(entry.title);
  const rename = useRenamePortfolioEntry();
  const fieldError = rename.error instanceof ApiError ? rename.error.fieldError('title') : undefined;

  function submit(e: FormEvent) {
    e.preventDefault();
    rename.mutate({ id: entry.id, title }, { onSuccess: onDone });
  }

  return (
    <form onSubmit={submit} className="space-y-2">
      <label htmlFor={id} className="block font-bold">Project name</label>
      <input
        id={id}
        className="input"
        value={title}
        maxLength={60}
        onChange={(e) => setTitle(e.target.value)}
        aria-invalid={fieldError ? true : undefined}
        aria-describedby={fieldError ? `${id}-err` : `${id}-hint`}
        autoFocus
      />
      {fieldError
        ? <p id={`${id}-err`} className="text-sm font-bold text-bad">{fieldError}</p>
        : <p id={`${id}-hint`} className="text-sm text-muted">Up to 60 characters. No real names or contact details.</p>}
      <div className="flex gap-2">
        <button type="submit" className="btn-primary" disabled={rename.isPending}>{rename.isPending ? 'Saving…' : 'Save'}</button>
        <button type="button" className="btn-secondary" onClick={onDone}>Cancel</button>
      </div>
      {!fieldError && <ErrorNote error={rename.error} />}
    </form>
  );
}
