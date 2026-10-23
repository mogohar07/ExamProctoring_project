// TypeScript mirrors of the backend DTOs. Property names are the JSON (camelCase) names the API sends and accepts.
// Source of truth: src/ExamProctoring.Application/Auth/*.cs and src/ExamProctoring.Application/Exams/*.cs.

// ---- Authentication (Application/Auth/TokenResponse.cs, MeResponse.cs) ----

export type Role = 'Admin' | 'Instructor' | 'Student';

export interface AuthUser {
  id: string;
  fullName: string;
  email: string;
  role: Role;
}

export interface TokenResponse {
  tokenType: string; // "Bearer"
  accessToken: string;
  accessTokenExpiresAt: string;
  refreshToken: string;
  refreshTokenExpiresAt: string;
  mustChangePassword: boolean;
  user: AuthUser;
}

// ---- Exams (Application/Exams/ExamRequest.cs, ExamResponse.cs, ExamListResponse.cs) ----

export type ExamStatus = 'Draft' | 'Published' | 'Cancelled';
export type ExamPhase = 'Upcoming' | 'Open' | 'Closed';

export interface Exam {
  id: string;
  title: string;
  description: string | null;
  startsAt: string; // ISO 8601, always UTC (+00:00) in responses
  durationMinutes: number;
  endsAt: string;
  status: ExamStatus;
  phase: ExamPhase;
  warningLimit: number; // set by the system, never sent by the client
  publishedAt: string | null;
}

export interface ExamPage {
  items: Exam[];
  page: number;
  pageSize: number;
  totalCount: number;
}

// startsAt must carry an explicit offset (Z or +hh:mm); the server converts it to UTC.
export interface ExamRequest {
  title: string;
  description: string | null;
  startsAt: string;
  durationMinutes: number;
}

// ---- Questions (Application/Exams/QuestionRequest.cs, QuestionResponse.cs) ----

export type QuestionType = 'Mcq' | 'Essay';

export interface QuestionOption {
  id: string;
  text: string;
  isCorrect: boolean;
  position: number;
}

export interface Question {
  id: string;
  examId: string;
  type: QuestionType;
  text: string;
  marks: number;
  modelAnswer: string | null;
  position: number;
  options: QuestionOption[];
}

export interface QuestionList {
  items: Question[];
}

export interface QuestionOptionRequest {
  text: string;
  isCorrect: boolean;
}

export interface QuestionRequest {
  type: QuestionType;
  text: string;
  marks: number;
  modelAnswer: string | null;
  options: QuestionOptionRequest[];
}

// ---- Errors (RFC 9457 ProblemDetails with the backend's stable "code" extension) ----

export interface ProblemDetails {
  status?: number;
  title?: string;
  detail?: string;
  code?: string;
  errors?: Record<string, string[]>;
}
