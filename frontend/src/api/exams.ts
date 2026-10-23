import type { Exam, ExamPage, ExamRequest, ExamStatus } from '../types/api';
import { apiRequest } from './client';

// ExamsController (instructor only; the backend returns only the caller's own exams).

export interface ExamListParams {
  status?: ExamStatus;
  page: number; // from 1
  pageSize: number; // 1-100
}

export function listExams(params: ExamListParams, signal?: AbortSignal): Promise<ExamPage> {
  const query = new URLSearchParams({ page: String(params.page), pageSize: String(params.pageSize) });
  if (params.status) query.set('status', params.status);
  return apiRequest<ExamPage>(`/api/exams?${query.toString()}`, { signal });
}

export function getExam(id: string, signal?: AbortSignal): Promise<Exam> {
  return apiRequest<Exam>(`/api/exams/${encodeURIComponent(id)}`, { signal });
}

export function createExam(request: ExamRequest): Promise<Exam> {
  return apiRequest<Exam>('/api/exams', { method: 'POST', body: request });
}

// Full replacement of title, description, start time and duration.
export function updateExam(id: string, request: ExamRequest): Promise<Exam> {
  return apiRequest<Exam>(`/api/exams/${encodeURIComponent(id)}`, { method: 'PUT', body: request });
}

// Drafts only; deletes the exam's questions too. 204.
export function deleteExam(id: string): Promise<void> {
  return apiRequest<void>(`/api/exams/${encodeURIComponent(id)}`, { method: 'DELETE' });
}

export type LifecycleAction = 'publish' | 'unpublish' | 'cancel';

// POST /api/exams/{id}/publish | /unpublish | /cancel. No body; 200 with the updated exam.
export function runLifecycleAction(id: string, action: LifecycleAction): Promise<Exam> {
  return apiRequest<Exam>(`/api/exams/${encodeURIComponent(id)}/${action}`, { method: 'POST' });
}
