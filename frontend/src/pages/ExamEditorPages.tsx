import { Link, useNavigate, useParams } from 'react-router-dom';
import { createExam, getExam, updateExam } from '../api/exams';
import { describeError } from '../api/errors';
import { ExamForm } from '../components/ExamForm';
import { Alert, Spinner } from '../components/Feedback';
import { useLoader } from '../hooks/useLoader';

// POST /api/exams: a new exam always starts as a Draft.
export function CreateExamPage() {
  const navigate = useNavigate();
  return (
    <div className="page page-narrow">
      <h1>New exam</h1>
      <p className="muted">The exam is created as a draft. Add questions, then publish it before its start time.</p>
      <ExamForm
        submitLabel="Create exam"
        onSubmit={async (request) => {
          const exam = await createExam(request);
          navigate(`/exams/${exam.id}`, { replace: true, state: { notice: 'Exam created.' } });
        }}
        onCancel={() => navigate('/exams')}
      />
    </div>
  );
}

// PUT /api/exams/{id}: allowed for Draft and Published exams before they start.
export function EditExamPage() {
  const { examId = '' } = useParams();
  const navigate = useNavigate();
  const exam = useLoader((signal) => getExam(examId, signal), [examId]);

  return (
    <div className="page page-narrow">
      <p>
        <Link to={`/exams/${examId}`}>← Back to exam</Link>
      </p>
      <h1>Edit exam</h1>
      {exam.loading && !exam.data && <Spinner label="Loading exam…" />}
      {exam.error != null && <Alert tone="error">{describeError(exam.error)}</Alert>}
      {exam.data && (exam.data.phase !== 'Upcoming' || exam.data.status === 'Cancelled') && (
        <Alert tone="info">
          This exam can no longer be edited because it {exam.data.status === 'Cancelled' ? 'is cancelled' : 'has started'}.
        </Alert>
      )}
      {exam.data && exam.data.phase === 'Upcoming' && exam.data.status !== 'Cancelled' && (
        <ExamForm
          exam={exam.data}
          submitLabel="Save changes"
          onSubmit={async (request) => {
            await updateExam(examId, request);
            navigate(`/exams/${examId}`, { replace: true, state: { notice: 'Exam saved.' } });
          }}
          onCancel={() => navigate(`/exams/${examId}`)}
        />
      )}
    </div>
  );
}
