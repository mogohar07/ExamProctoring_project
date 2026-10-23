import type { TokenResponse } from '../types/api';
import { apiRequest } from './client';

// POST /api/auth/login (AuthController). Anonymous; rate-limited per IP by the backend.
export function login(email: string, password: string): Promise<TokenResponse> {
  return apiRequest<TokenResponse>('/api/auth/login', { method: 'POST', body: { email, password }, auth: false });
}

// POST /api/auth/change-password. Allowed while a password change is pending; returns a new token pair and ends
// every earlier session of the account.
export function changePassword(currentPassword: string, newPassword: string): Promise<TokenResponse> {
  return apiRequest<TokenResponse>('/api/auth/change-password', {
    method: 'POST',
    body: { currentPassword, newPassword },
  });
}

// POST /api/auth/logout: ends the session (token family) of the given refresh token. 204 on success.
export function logout(refreshToken: string): Promise<void> {
  return apiRequest<void>('/api/auth/logout', { method: 'POST', body: { refreshToken } });
}
