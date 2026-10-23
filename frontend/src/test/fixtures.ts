import type { Exam, Role, TokenResponse } from '../types/api';

export function tokenResponse(overrides: { role?: Role; mustChangePassword?: boolean; accessToken?: string } = {}): TokenResponse {
  return {
    tokenType: 'Bearer',
    accessToken: overrides.accessToken ?? 'access-1',
    accessTokenExpiresAt: new Date(Date.now() + 15 * 60_000).toISOString(),
    refreshToken: 'refresh-1',
    refreshTokenExpiresAt: new Date(Date.now() + 7 * 86_400_000).toISOString(),
    mustChangePassword: overrides.mustChangePassword ?? false,
    user: { id: 'u1', fullName: 'Test Instructor', email: 'instructor@example.test', role: overrides.role ?? 'Instructor' },
  };
}

export function exam(overrides: Partial<Exam> = {}): Exam {
  return {
    id: 'e1',
    title: 'Midterm',
    description: null,
    startsAt: '2030-01-01T09:00:00+00:00',
    durationMinutes: 90,
    endsAt: '2030-01-01T10:30:00+00:00',
    status: 'Draft',
    phase: 'Upcoming',
    warningLimit: 20,
    publishedAt: null,
    ...overrides,
  };
}

export function jsonResponse(status: number, body?: unknown, contentType = 'application/json'): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: body === undefined ? {} : { 'Content-Type': contentType },
  });
}

export function problem(status: number, code: string | undefined, detail: string, errors?: Record<string, string[]>): Response {
  return jsonResponse(status, { status, detail, code, errors }, 'application/problem+json');
}
