import type { AuthUser, TokenResponse } from '../types/api';

// The signed-in session. The backend returns tokens in JSON (no cookies):
// - the access token (short-lived, 15 minutes by default) is kept in memory only;
// - the refresh token (7 days by default, rotated on every use) is kept in sessionStorage so a page reload in the
//   same tab can continue the session. It is cleared when the tab is closed. Any script running on the page could read
//   it, so the app must not render untrusted HTML (React escapes text by default).

export interface Session {
  accessToken: string;
  accessTokenExpiresAt: number; // epoch milliseconds
  user: AuthUser;
  mustChangePassword: boolean;
}

type Listener = (session: Session | null) => void;

const REFRESH_TOKEN_KEY = 'examProctoring.refreshToken';

let session: Session | null = null;
const listeners = new Set<Listener>();

function readStorage(): Storage | null {
  try {
    return window.sessionStorage;
  } catch {
    return null; // storage blocked: the session simply won't survive a reload
  }
}

export const tokenStore = {
  getSession(): Session | null {
    return session;
  },

  getRefreshToken(): string | null {
    try {
      return readStorage()?.getItem(REFRESH_TOKEN_KEY) ?? null;
    } catch {
      return null;
    }
  },

  setFromTokenResponse(tokens: TokenResponse): void {
    session = {
      accessToken: tokens.accessToken,
      accessTokenExpiresAt: Date.parse(tokens.accessTokenExpiresAt),
      user: tokens.user,
      mustChangePassword: tokens.mustChangePassword,
    };
    try {
      readStorage()?.setItem(REFRESH_TOKEN_KEY, tokens.refreshToken);
    } catch {
      // ignore: see readStorage
    }
    listeners.forEach((listener) => listener(session));
  },

  clear(): void {
    session = null;
    try {
      readStorage()?.removeItem(REFRESH_TOKEN_KEY);
    } catch {
      // ignore
    }
    listeners.forEach((listener) => listener(null));
  },

  subscribe(listener: Listener): () => void {
    listeners.add(listener);
    return () => listeners.delete(listener);
  },
};
