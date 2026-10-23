import { useState } from 'react';
import { Link } from 'react-router-dom';
import { deleteExam, runLifecycleAction, type LifecycleAction } from '../api/exams';
import { ApiError, describeError } from '../api/errors';
import type { Exam } from '../types/api';
import { ConfirmDialog } from './ConfirmDialog';
import { Alert } from './Feedback';

type PendingAction = LifecycleAction | 'delete';

const CONFIRMATIONS: Record<PendingAction, { title: string; body: string; label: string; tone: 'danger' | 'primary' }> = {
  publish: {
    title: 'Publish this exam?',
    body: 'The server checks every question first. Until the exam starts you can still edit it or unpublish it.',
    label: 'Publish',
    tone: 'primary',
  },
  unpublish: {
    title: 'Return this exam to Draft?',
    body: 'The exam stops being published and its publication time is cleared. You can publish it again before it starts.',
    label: 'Unpublish',
    tone: 'primary',
  },
  cancel: {
    title: 'Cancel this exam permanently?',
    body: 'Cancelling cannot be undone. A cancelled exam stays in your list, but it can no longer be edited, published, unpublished or deleted.',
    label: 'Cancel exam',
    tone: 'danger',
  },
  delete: {
    title: 'Delete this draft exam?',
    body: 'The exam and all of its questions are deleted permanently. This cannot be undone.',
    label: 'Delete',
    tone: 'danger',
  },
};

interface ExamActionsProps {
  exam: Exam;
  onChanged(exam: Exam): void;
  onDeleted(): void;
  // A refusal can mean the exam changed on the server (for example it started): reload it.
  onStale(): void;
}

// Buttons follow the exam's status and phase as last loaded, but the backend decides: a refused action shows the
// server's own explanation (the ProblemDetails detail), and the page reloads the exam afterwards.
export function ExamActions({ exam, onChanged, onDeleted, onStale }: ExamActionsProps) {
  const [pending, setPending] = useState<PendingAction | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ tone: 'error' | 'success'; text: string } | null>(null);

  const beforeStart = exam.phase === 'Upcoming';
  const canEdit = beforeStart && exam.status !== 'Cancelled';

  const run = async () => {
    if (!pending) return;
    setBusy(true);
    setMessage(null);
    try {
      if (pending === 'delete') {
        await deleteExam(exam.id);
        setPending(null);
        onDeleted();
        return;
      }
      const updated = await runLifecycleAction(exam.id, pending);
      onChanged(updated);
      setMessage({ tone: 'success', text: successText(pending) });
    } catch (error) {
      setMessage({ tone: 'error', text: describeError(error) });
      if (error instanceof ApiError && (error.status === 409 || error.status === 404)) onStale();
    } finally {
      setBusy(false);
      setPending(null);
    }
  };

  return (
    <div className="exam-actions">
      {message && (
        <Alert tone={message.tone} onDismiss={() => setMessage(null)}>
          {message.text}
        </Alert>
      )}

      <div className="button-row">
        {canEdit && (
          <Link className="button button-secondary" to={`/exams/${exam.id}/edit`}>
            Edit details
          </Link>
        )}
        {exam.status === 'Draft' && (
          <button type="button" className="button" disabled={!beforeStart} onClick={() => setPending('publish')}>
            Publish
          </button>
        )}
        {exam.status === 'Published' && (
          <>
            <button
              type="button"
              className="button button-secondary"
              disabled={!beforeStart}
              onClick={() => setPending('unpublish')}
            >
              Unpublish
            </button>
            <button type="button" className="button button-danger" disabled={!beforeStart} onClick={() => setPending('cancel')}>
              Cancel exam
            </button>
          </>
        )}
        {exam.status === 'Draft' && (
          <button type="button" className="button button-danger-outline" onClick={() => setPending('delete')}>
            Delete
          </button>
        )}
      </div>

      {exam.status === 'Cancelled' && <p className="muted">This exam is cancelled. Cancelled exams can't be changed.</p>}
      {exam.status !== 'Cancelled' && !beforeStart && (
        <p className="muted">
          This exam has started, so it can no longer be edited, published, unpublished or cancelled.
          {exam.status === 'Draft' ? ' A draft can still be deleted.' : ''}
        </p>
      )}

      {pending && (
        <ConfirmDialog
          open
          title={CONFIRMATIONS[pending].title}
          confirmLabel={CONFIRMATIONS[pending].label}
          tone={CONFIRMATIONS[pending].tone}
          busy={busy}
          onConfirm={() => void run()}
          onCancel={() => setPending(null)}
        >
          <p>{CONFIRMATIONS[pending].body}</p>
        </ConfirmDialog>
      )}
    </div>
  );
}

function successText(action: LifecycleAction): string {
  switch (action) {
    case 'publish':
      return 'The exam is published.';
    case 'unpublish':
      return 'The exam is back in Draft.';
    case 'cancel':
      return 'The exam is cancelled.';
  }
}
