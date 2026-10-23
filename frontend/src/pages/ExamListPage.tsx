import { Link, useSearchParams } from 'react-router-dom';
import { describeError } from '../api/errors';
import { listExams } from '../api/exams';
import { Alert, EmptyState, Spinner } from '../components/Feedback';
import { Pagination } from '../components/Pagination';
import { PhaseBadge, StatusBadge } from '../components/StatusBadge';
import { useLoader } from '../hooks/useLoader';
import type { ExamStatus } from '../types/api';
import { formatDateTime, formatDuration } from '../utils/dates';

const STATUSES: ExamStatus[] = ['Draft', 'Published', 'Cancelled'];
const PAGE_SIZES = [10, 25, 50];

// GET /api/exams: the instructor's own exams, newest start first, paged (pageSize 1-100, default 10), with an
// optional status filter. Filter and page live in the URL so the browser's back button works.
export function ExamListPage() {
  const [params, setParams] = useSearchParams();
  const status = STATUSES.find((value) => value === params.get('status'));
  const page = Math.max(1, Number.parseInt(params.get('page') ?? '1', 10) || 1);
  const pageSize = PAGE_SIZES.includes(Number(params.get('pageSize'))) ? Number(params.get('pageSize')) : 10;

  const exams = useLoader((signal) => listExams({ status, page, pageSize }, signal), [status, page, pageSize]);

  const setQuery = (changes: Record<string, string | undefined>) => {
    const next = new URLSearchParams(params);
    Object.entries(changes).forEach(([key, value]) => (value ? next.set(key, value) : next.delete(key)));
    setParams(next);
  };

  return (
    <div className="page">
      <div className="page-header">
        <div>
          <h1>My exams</h1>
          <p className="muted">Times are shown in your local time zone.</p>
        </div>
        <Link to="/exams/new" className="button">
          New exam
        </Link>
      </div>

      <div className="toolbar">
        <label className="inline-field">
          Status
          <select value={status ?? ''} onChange={(event) => setQuery({ status: event.target.value || undefined, page: undefined })}>
            <option value="">All</option>
            {STATUSES.map((value) => (
              <option key={value} value={value}>
                {value}
              </option>
            ))}
          </select>
        </label>
        <label className="inline-field">
          Per page
          <select value={pageSize} onChange={(event) => setQuery({ pageSize: event.target.value, page: undefined })}>
            {PAGE_SIZES.map((size) => (
              <option key={size} value={size}>
                {size}
              </option>
            ))}
          </select>
        </label>
        {exams.loading && exams.data && <Spinner label="Updating…" />}
      </div>

      {exams.error != null && (
        <Alert tone="error">
          {describeError(exams.error)}{' '}
          <button type="button" className="link-button" onClick={exams.reload}>
            Try again
          </button>
        </Alert>
      )}

      {exams.loading && !exams.data && <Spinner label="Loading exams…" />}

      {exams.data && exams.data.items.length === 0 && (
        <EmptyState title={status ? `No ${status.toLowerCase()} exams` : 'No exams yet'}>
          {exams.data.totalCount > 0 ? (
            <p>This page is past the end of the list.</p>
          ) : (
            <p>
              <Link to="/exams/new">Create your first exam</Link>.
            </p>
          )}
        </EmptyState>
      )}

      {exams.data && exams.data.items.length > 0 && (
        <>
          <table className="exam-table">
            <thead>
              <tr>
                <th scope="col">Title</th>
                <th scope="col">Starts</th>
                <th scope="col">Duration</th>
                <th scope="col">Status</th>
                <th scope="col">Phase</th>
              </tr>
            </thead>
            <tbody>
              {exams.data.items.map((exam) => (
                <tr key={exam.id}>
                  <td data-label="Title">
                    <Link to={`/exams/${exam.id}`} className="exam-title-link">
                      {exam.title}
                    </Link>
                    {exam.description && <div className="muted truncate">{exam.description}</div>}
                  </td>
                  <td data-label="Starts">{formatDateTime(exam.startsAt)}</td>
                  <td data-label="Duration">{formatDuration(exam.durationMinutes)}</td>
                  <td data-label="Status">
                    <StatusBadge status={exam.status} />
                  </td>
                  <td data-label="Phase">
                    <PhaseBadge phase={exam.phase} />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          <Pagination
            page={exams.data.page}
            pageSize={exams.data.pageSize}
            totalCount={exams.data.totalCount}
            onPageChange={(next) => setQuery({ page: String(next) })}
          />
        </>
      )}
    </div>
  );
}
