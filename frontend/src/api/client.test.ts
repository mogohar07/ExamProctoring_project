import { describe, expect, it, vi } from 'vitest';
import { tokenStore } from '../auth/tokenStore';
import { jsonResponse, problem, tokenResponse } from '../test/fixtures';
import { apiRequest } from './client';
import { ApiError, describeError } from './errors';

function mockFetch(...responses: Array<Response | Error>) {
  const fetchMock = vi.fn();
  responses.forEach((response) =>
    response instanceof Error ? fetchMock.mockRejectedValueOnce(response) : fetchMock.mockResolvedValueOnce(response),
  );
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

function authorizationOf(call: unknown[]): string | undefined {
  const init = call[1] as RequestInit;
  return (init.headers as Record<string, string>).Authorization;
}

async function caught(promise: Promise<unknown>): Promise<ApiError> {
  try {
    await promise;
  } catch (error) {
    if (error instanceof ApiError) return error;
    throw error;
  }
  throw new Error('Expected the request to fail.');
}

describe('apiRequest', () => {
  it('sends JSON with the bearer token and returns the parsed body', async () => {
    tokenStore.setFromTokenResponse(tokenResponse());
    const fetchMock = mockFetch(jsonResponse(200, { items: [], page: 1, pageSize: 10, totalCount: 0 }));

    const result = await apiRequest('/api/exams?page=1&pageSize=10');

    expect(result).toEqual({ items: [], page: 1, pageSize: 10, totalCount: 0 });
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(authorizationOf(fetchMock.mock.calls[0])).toBe('Bearer access-1');
  });

  it('returns undefined for 204 No Content', async () => {
    tokenStore.setFromTokenResponse(tokenResponse());
    mockFetch(new Response(null, { status: 204 }));

    await expect(apiRequest('/api/exams/e1', { method: 'DELETE' })).resolves.toBeUndefined();
  });

  it('turns a 400 ProblemDetails into field errors', async () => {
    tokenStore.setFromTokenResponse(tokenResponse());
    mockFetch(problem(400, 'validation_failed', 'One or more fields are invalid.', { 'options[1].text': ['Option text is required.'] }));

    const error = await caught(apiRequest('/api/exams/e1/questions', { method: 'POST', body: {} }));

    expect(error.status).toBe(400);
    expect(error.code).toBe('validation_failed');
    expect(error.fieldErrors['options[1].text']).toEqual(['Option text is required.']);
  });

  it('keeps the backend detail of a lifecycle conflict for display', async () => {
    tokenStore.setFromTokenResponse(tokenResponse());
    mockFetch(problem(409, 'exam_not_publishable', 'The exam has no questions.'));

    const error = await caught(apiRequest('/api/exams/e1/publish', { method: 'POST' }));

    expect(error.code).toBe('exam_not_publishable');
    expect(describeError(error)).toBe('The exam has no questions.');
  });

  it('reports an unreachable server as a network error', async () => {
    tokenStore.setFromTokenResponse(tokenResponse());
    mockFetch(new TypeError('Failed to fetch'));

    const error = await caught(apiRequest('/api/exams'));

    expect(error.kind).toBe('network');
    expect(describeError(error)).toMatch(/Cannot reach the server/);
  });

  it('never shows server error details to the user', async () => {
    tokenStore.setFromTokenResponse(tokenResponse());
    mockFetch(problem(500, undefined, 'System.InvalidOperationException: internal detail'));

    const error = await caught(apiRequest('/api/exams'));

    expect(describeError(error)).toBe('The server had a problem. Please try again later.');
  });

  it('refreshes once after 401 token_invalid and retries with the new token', async () => {
    tokenStore.setFromTokenResponse(tokenResponse());
    const fetchMock = mockFetch(
      problem(401, 'token_invalid', 'A valid access token is required.'),
      jsonResponse(200, tokenResponse({ accessToken: 'access-2' })),
      jsonResponse(200, { id: 'e1' }),
    );

    const result = await apiRequest('/api/exams/e1');

    expect(result).toEqual({ id: 'e1' });
    expect(fetchMock).toHaveBeenCalledTimes(3);
    expect(fetchMock.mock.calls[1][0]).toBe('/api/auth/refresh');
    expect(authorizationOf(fetchMock.mock.calls[1])).toBeUndefined();
    expect(authorizationOf(fetchMock.mock.calls[2])).toBe('Bearer access-2');
  });

  it('signs out when the refresh token is rejected', async () => {
    tokenStore.setFromTokenResponse(tokenResponse());
    mockFetch(
      problem(401, 'token_invalid', 'A valid access token is required.'),
      problem(401, 'refresh_token_reused', 'The refresh token was already used.'),
    );

    const error = await caught(apiRequest('/api/exams'));

    expect(error.status).toBe(401);
    expect(tokenStore.getSession()).toBeNull();
    expect(tokenStore.getRefreshToken()).toBeNull();
  });

  it('does not try to refresh after a login failure', async () => {
    const fetchMock = mockFetch(problem(401, 'invalid_credentials', 'The email or password is incorrect.'));

    const error = await caught(apiRequest('/api/auth/login', { method: 'POST', body: {}, auth: false }));

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(describeError(error)).toBe('The email or password is incorrect.');
  });
});
