import type { ProblemDetails } from '../types/api';

export type ApiErrorKind = 'http' | 'network' | 'aborted';

// One error type for every failed call. For HTTP errors, code, detail and fieldErrors come from the backend's
// ProblemDetails body (application/problem+json).
export class ApiError extends Error {
  readonly kind: ApiErrorKind;
  readonly status: number; // 0 when no response was received
  readonly code?: string;
  readonly detail?: string;
  readonly fieldErrors: Record<string, string[]>;

  constructor(kind: ApiErrorKind, status: number, problem: ProblemDetails = {}) {
    super(problem.detail ?? (kind === 'network' ? 'Network error' : `HTTP ${status}`));
    this.name = 'ApiError';
    this.kind = kind;
    this.status = status;
    this.code = problem.code;
    this.detail = problem.detail;
    this.fieldErrors = problem.errors ?? {};
  }
}

export function isAbortError(error: unknown): boolean {
  return error instanceof ApiError && error.kind === 'aborted';
}

// A message that is safe to show to the user. Server-error details are never shown, because in Development the backend
// may put internal exception messages there.
export function describeError(error: unknown): string {
  if (!(error instanceof ApiError)) {
    return 'Something went wrong. Please try again.';
  }

  if (error.kind === 'network') {
    return 'Cannot reach the server. Check that the backend is running and the API address is correct.';
  }

  switch (error.status) {
    case 400:
      return error.detail ?? 'Some fields are invalid. Check the form and try again.';
    case 401:
      return error.code === 'token_invalid' || !error.code
        ? 'Your session has ended. Please sign in again.'
        : error.detail ?? 'Sign-in failed.';
    case 403:
      if (error.code === 'password_change_required') return 'You must change your password before continuing.';
      if (error.code) return error.detail ?? 'This action is not allowed.';
      return 'Your account does not have access to this feature. This dashboard is for instructor accounts.';
    case 404:
      if (error.code === 'exam_not_found') return 'This exam was not found, or it belongs to another instructor.';
      if (error.code === 'question_not_found') return 'This question was not found, or it belongs to another instructor.';
      return 'The requested item was not found.';
    case 409:
      return error.detail ?? 'This action is not allowed in the exam\'s current state.';
    case 413:
      return 'The request is too large.';
    case 429:
      return 'Too many attempts. Please wait a minute and try again.';
    default:
      return error.status >= 500
        ? 'The server had a problem. Please try again later.'
        : error.detail ?? `Request failed (HTTP ${error.status}).`;
  }
}
