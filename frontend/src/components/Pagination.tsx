interface PaginationProps {
  page: number;
  pageSize: number;
  totalCount: number;
  onPageChange(page: number): void;
}

export function Pagination({ page, pageSize, totalCount, onPageChange }: PaginationProps) {
  const pageCount = Math.max(1, Math.ceil(totalCount / pageSize));
  const first = totalCount === 0 ? 0 : (page - 1) * pageSize + 1;
  const last = Math.min(page * pageSize, totalCount);

  return (
    <nav className="pagination" aria-label="Exam list pages">
      <span className="pagination-summary">
        {totalCount === 0 ? 'No exams' : `${first}–${last} of ${totalCount}`}
      </span>
      <div className="pagination-buttons">
        <button type="button" className="button button-secondary" disabled={page <= 1} onClick={() => onPageChange(page - 1)}>
          Previous
        </button>
        <span className="pagination-page">
          Page {page} of {pageCount}
        </span>
        <button
          type="button"
          className="button button-secondary"
          disabled={page >= pageCount}
          onClick={() => onPageChange(page + 1)}
        >
          Next
        </button>
      </div>
    </nav>
  );
}
