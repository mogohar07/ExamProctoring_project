import { tokenStore } from '../auth/tokenStore';
import type { ProblemDetails, TokenResponse } from '../types/api';
import { ApiError } from './errors';

// Empty base URL = same origin (the Vite dev server proxies /api to the backend). See .env.example.
const BASE_URL = (import.meta.env.VITE_API_BASE_URL ?? '').replace(/\/+$/, '');

// Refresh a little before the access token expires instead of waiting for a 401.
const REFRESH_MARGIN_MS = 30_000;

export type HttpMethod = 'GET' | 'POST' | 'PUT' | 'DELETE';

export interface RequestOptions {
  method?: HttpMethod;
  body?: unknown;
  signal?: AbortSignal;
  // false for the anonymous endpoints (login, refresh).
  auth?: boolean;
}

// Every API call goes through here: JSON in and out, the bearer token, ProblemDetails errors, and one transparent
// token refresh when the access token has expired.
export async function apiRequest<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const useAuth = options.auth ?? true;

  if (useAuth) {
    const session = tokenStore.getSession();
    if (session && session.accessTokenExpiresAt - Date.now() < REFRESH_MARGIN_MS) {
      await refreshSession();
    }
  }

  try {
    return await send<T>(path, options);
  } catch (error) {
    const expired =
      useAuth &&
      error instanceof ApiError &&
      error.status === 401 &&
      (!error.code || error.code === 'token_invalid') &&
      tokenStore.getRefreshToken() !== null;
    if (expired && (await refreshSession())) {
      return send<T>(path, options);
    }
    throw error;
  }
}

let refreshInFlight: Promise<boolean> | null = null;

// Exchanges the stored refresh token for a new token pair. Only one refresh runs at a time: the backend rotates
// refresh tokens, and presenting an already-used one is treated as reuse and ends the session.
// Resolves false (and signs out) when the refresh token is rejected; throws on network errors.
export function refreshSession(): Promise<boolean> {
  if (!refreshInFlight) {
    refreshInFlight = runRefresh().finally(() => {
      refreshInFlight = null;
    });
  }
  return refreshInFlight;
}

async function runRefresh(): Promise<boolean> {
  const refreshToken = tokenStore.getRefreshToken();
  if (!refreshToken) {
    tokenStore.clear();
    return false;
  }

  try {
    const tokens = await send<TokenResponse>('/api/auth/refresh', {
      method: 'POST',
      body: { refreshToken },
      auth: false,
    });
    tokenStore.setFromTokenResponse(tokens);
    return true;
  } catch (error) {
    if (error instanceof ApiError && error.kind === 'network') {
      throw error; // the server is unreachable; keep the session and let the caller report it
    }
    tokenStore.clear();
    return false;
  }
}

async function send<T>(path: string, options: RequestOptions): Promise<T> {
  const { method = 'GET', body, signal, auth = true } = options;

  const headers: Record<string, string> = { Accept: 'application/json' };
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  const accessToken = auth ? tokenStore.getSession()?.accessToken : undefined;
  if (accessToken) headers.Authorization = `Bearer ${accessToken}`;

  let response: Response;
  try {
    response = await fetch(`${BASE_URL}${path}`, {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
      signal,
    });
  } catch {
    throw new ApiError(signal?.aborted ? 'aborted' : 'network', 0);
  }

  if (response.status === 204) return undefined as T;

  let payload: unknown;
  try {
    const text = await response.text();
    payload = text ? JSON.parse(text) : undefined;
  } catch {
    if (signal?.aborted) throw new ApiError('aborted', 0);
    payload = undefined; // not JSON (for example an HTML error page from a proxy)
  }

  if (!response.ok) {
    const problem = typeof payload === 'object' && payload !== null ? (payload as ProblemDetails) : {};
    throw new ApiError('http', response.status, problem);
  }

  return payload as T;
}
