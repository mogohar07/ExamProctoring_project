import { useState } from 'react';
import { ApiError, describeError } from '../api/errors';
import { createQuestion, deleteQuestion, listQuestions, updateQuestion } from '../api/questions';
import { useLoader } from '../hooks/useLoader';
import type { Exam, Question, QuestionRequest } from '../types/api';
import { ConfirmDialog } from './ConfirmDialog';
import { Alert, EmptyState, Spinner } from './Feedback';
import { QuestionForm } from './QuestionForm';

interface QuestionsPanelProps {
  exam: Exam;
  // The exam may have changed on the server (for example it started or was cancelled): reload it.
  onExamStale(): void;
}

// Questions can be read at any time. Writes are allowed only while the exam is Draft or Published and has not started;
// the server checks this on every write and answers 409 exam_not_editable otherwise.
export function QuestionsPanel({ exam, onExamStale }: QuestionsPanelProps) {
  const questions = useLoader((signal) => listQuestions(exam.id, signal), [exam.id]);
  const [editing, setEditing] = useState<string | 'new' | null>(null);
  const [toDelete, setToDelete] = useState<Question | null>(null);
  const [deleting, setDeleting] = useState(false);
  const [message, setMessage] = useState<{ tone: 'error' | 'success'; text: string } | null>(null);

  const editable = exam.phase === 'Upcoming' && exam.status !== 'Cancelled';

  const afterWrite = (text: string) => {
    setEditing(null);
    setMessage({ tone: 'success', text });
    questions.reload(); // positions may have changed: show the server's list
  };

  const handleFailure = (error: unknown) => {
    if (error instanceof ApiError && (error.status === 409 || error.status === 404)) onExamStale();
    throw error; // the form shows the message and field errors
  };

  const save = async (request: QuestionRequest, question?: Question) => {
    try {
      if (question) await updateQuestion(question.id, request);
      else await createQuestion(exam.id, request);
      afterWrite(question ? 'Question saved.' : 'Question added.');
    } catch (error) {
      handleFailure(error);
    }
  };

  const confirmDelete = async () => {
    if (!toDelete) return;
    setDeleting(true);
    try {
      await deleteQuestion(toDelete.id);
      setToDelete(null);
      afterWrite('Question deleted.');
    } catch (error) {
      setToDelete(null);
      setMessage({ tone: 'error', text: describeError(error) });
      if (error instanceof ApiError && (error.status === 409 || error.status === 404)) onExamStale();
    } finally {
      setDeleting(false);
    }
  };

  const items = questions.data?.items ?? [];
  const totalMarks = items.reduce((sum, question) => sum + (question.marks ?? 0), 0);
  const withoutMarks = items.filter((question) => question.marks === null).length;

  return (
    <section className="card" aria-labelledby="questions-heading">
      <div className="section-header">
        <div>
          <h2 id="questions-heading">Questions</h2>
          {questions.data && (
            <p className="muted">
              {items.length} {items.length === 1 ? 'question' : 'questions'} · {totalMarks.toFixed(2)} marks in total
              {withoutMarks > 0 && ` · ${withoutMarks} without marks (add them before publishing)`}
            </p>
          )}
        </div>
        {editable && editing !== 'new' && (
          <button type="button" className="button" onClick={() => setEditing('new')}>
            Add question
          </button>
        )}
      </div>

      {!editable && (
        <p className="muted">
          {exam.status === 'Cancelled'
            ? 'This exam is cancelled, so its questions are read-only.'
            : 'This exam has started, so its questions are read-only.'}
        </p>
      )}

      {message && (
        <Alert tone={message.tone} onDismiss={() => setMessage(null)}>
          {message.text}
        </Alert>
      )}

      {editing === 'new' && (
        <div className="question-editor">
          <h3>New question</h3>
          <QuestionForm onSubmit={(request) => save(request)} onCancel={() => setEditing(null)} />
        </div>
      )}

      {questions.loading && !questions.data && <Spinner label="Loading questions…" />}
      {questions.error != null && (
        <Alert tone="error">
          {describeError(questions.error)}{' '}
          <button type="button" className="link-button" onClick={questions.reload}>
            Try again
          </button>
        </Alert>
      )}

      {questions.data && items.length === 0 && editing !== 'new' && (
        <EmptyState title="No questions yet">
          <p>{editable ? 'Add at least one question before publishing.' : 'This exam has no questions.'}</p>
        </EmptyState>
      )}

      <ol className="question-list">
        {items.map((question) =>
          editing === question.id ? (
            <li key={question.id} className="question-item question-editor">
              <h3>Edit question {question.position}</h3>
              <QuestionForm question={question} onSubmit={(request) => save(request, question)} onCancel={() => setEditing(null)} />
            </li>
          ) : (
            <li key={question.id} className="question-item">
              <div className="question-head">
                <span className="question-number">Q{question.position}</span>
                <span className="badge badge-neutral">{question.type === 'Mcq' ? 'MCQ' : 'Essay'}</span>
                <span className="question-marks">
                  {question.marks === null ? 'No marks yet' : `${question.marks.toFixed(2)} marks`}
                </span>
              </div>
              <p className="question-text">{question.text}</p>
              {question.type === 'Mcq' && (
                <ul className="option-list">
                  {question.options.map((option) => (
                    <li key={option.id} className={option.isCorrect ? 'option-correct' : undefined}>
                      {option.text}
                      {option.isCorrect && <span className="sr-only"> (correct answer)</span>}
                      {option.isCorrect && <span aria-hidden="true"> ✓</span>}
                    </li>
                  ))}
                </ul>
              )}
              {question.modelAnswer && (
                <p className="model-answer">
                  <strong>Model answer:</strong> {question.modelAnswer}
                </p>
              )}
              {editable && (
                <div className="button-row">
                  <button type="button" className="button button-secondary button-small" onClick={() => setEditing(question.id)}>
                    Edit
                  </button>
                  <button type="button" className="button button-danger-outline button-small" onClick={() => setToDelete(question)}>
                    Delete
                  </button>
                </div>
              )}
            </li>
          ),
        )}
      </ol>

      <ConfirmDialog
        open={toDelete !== null}
        title="Delete this question?"
        confirmLabel="Delete"
        tone="danger"
        busy={deleting}
        onConfirm={() => void confirmDelete()}
        onCancel={() => setToDelete(null)}
      >
        <p>
          Question {toDelete?.position} and its options are deleted permanently. Later questions move up one position.
          {exam.status === 'Published' && ' A published exam must keep at least one question.'}
        </p>
      </ConfirmDialog>
    </section>
  );
}
