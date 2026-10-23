import { useState } from 'react';
import { Link, useLocation, useNavigate, useParams } from 'react-router-dom';
import { describeError } from '../api/errors';
import { getExam } from '../api/exams';
import { ExamActions } from '../components/ExamActions';
import { Alert, Spinner } from '../components/Feedback';
import { QuestionsPanel } from '../components/QuestionsPanel';
import { PhaseBadge, StatusBadge } from '../components/StatusBadge';
import { useLoader } from '../hooks/useLoader';
import { formatDateTime, formatDuration } from '../utils/dates';

// GET /api/exams/{id} with the lifecycle actions and the question list.
export function ExamDetailsPage() {
  const { examId = '' } = useParams();
  const navigate = useNavigate();
  const location = useLocation();
  const [notice, setNotice] = useState<string | null>((location.state as { notice?: string } | null)?.notice ?? null);
  const exam = useLoader((signal) => getExam(examId, signal), [examId]);

  if (exam.loading && !exam.data) return <Spinner label="Loading exam…" />;

  if (!exam.data) {
    return (
      <div className="page">
        <Alert tone="error">{describeError(exam.error)}</Alert>
        <Link to="/exams">Back to my exams</Link>
      </div>
    );
  }

  const data = exam.data;

  return (
    <div className="page">
      <p>
        <Link to="/exams">← My exams</Link>
      </p>

      {notice && (
        <Alert tone="success" onDismiss={() => setNotice(null)}>
          {notice}
        </Alert>
      )}
      {exam.error != null && <Alert tone="error">{describeError(exam.error)}</Alert>}

      <section className="card" aria-labelledby="exam-heading">
        <div className="section-header">
          <div>
            <h1 id="exam-heading">{data.title}</h1>
            <div className="badge-row">
              <StatusBadge status={data.status} />
              <PhaseBadge phase={data.phase} />
              {exam.loading && <Spinner label="Refreshing…" />}
            </div>
          </div>
        </div>

        {data.description && <p className="exam-description">{data.description}</p>}

        <dl className="details-grid">
          <div>
            <dt>Starts</dt>
            <dd>{formatDateTime(data.startsAt)}</dd>
          </div>
          <div>
            <dt>Ends</dt>
            <dd>{formatDateTime(data.endsAt)}</dd>
          </div>
          <div>
            <dt>Duration</dt>
            <dd>{formatDuration(data.durationMinutes)}</dd>
          </div>
          <div>
            <dt>Published</dt>
            <dd>{data.publishedAt ? formatDateTime(data.publishedAt) : 'Not published'}</dd>
          </div>
          <div>
            <dt>Warning limit</dt>
            <dd>{data.warningLimit} (set by the system)</dd>
          </div>
        </dl>

        <ExamActions
          exam={data}
          onChanged={(updated) => exam.setData(updated)}
          onDeleted={() => navigate('/exams', { replace: true })}
          onStale={exam.reload}
        />
      </section>

      <QuestionsPanel exam={data} onExamStale={exam.reload} />
    </div>
  );
}
