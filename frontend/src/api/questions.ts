import type { Question, QuestionList, QuestionRequest } from '../types/api';
import { apiRequest } from './client';

// QuestionsController (instructor only, own exams).

// All questions in position order, not paged. Allowed whatever the exam's status or time.
export function listQuestions(examId: string, signal?: AbortSignal): Promise<QuestionList> {
  return apiRequest<QuestionList>(`/api/exams/${encodeURIComponent(examId)}/questions`, { signal });
}

// Appends the question after the last one.
export function createQuestion(examId: string, request: QuestionRequest): Promise<Question> {
  return apiRequest<Question>(`/api/exams/${encodeURIComponent(examId)}/questions`, { method: 'POST', body: request });
}

// Full replacement: the options sent replace all existing options (they get new ids).
export function updateQuestion(id: string, request: QuestionRequest): Promise<Question> {
  return apiRequest<Question>(`/api/questions/${encodeURIComponent(id)}`, { method: 'PUT', body: request });
}

// Later questions move up one position. 204.
export function deleteQuestion(id: string): Promise<void> {
  return apiRequest<void>(`/api/questions/${encodeURIComponent(id)}`, { method: 'DELETE' });
}
