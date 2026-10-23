import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { changePassword as changePasswordRequest, login as loginRequest, logout as logoutRequest } from '../api/auth';
import { refreshSession } from '../api/client';
import { tokenStore, type Session } from './tokenStore';

export type AuthStatus = 'loading' | 'signedIn' | 'signedOut';

interface AuthContextValue {
  status: AuthStatus;
  session: Session | null;
  signIn(email: string, password: string): Promise<Session>;
  changePassword(currentPassword: string, newPassword: string): Promise<Session>;
  signOut(): Promise<void>;
}

const AuthContext = createContext<AuthContextValue | null>(null);

function initialStatus(): AuthStatus {
  if (tokenStore.getSession()) return 'signedIn';
  return tokenStore.getRefreshToken() ? 'loading' : 'signedOut';
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [session, setSession] = useState<Session | null>(() => tokenStore.getSession());
  const [status, setStatus] = useState<AuthStatus>(initialStatus);

  useEffect(
    () =>
      tokenStore.subscribe((next) => {
        setSession(next);
        setStatus(next ? 'signedIn' : 'signedOut');
      }),
    [],
  );

  // After a page reload only the refresh token is left (the access token lived in memory): exchange it once.
  useEffect(() => {
    if (status !== 'loading') return;
    refreshSession()
      .then((ok) => setStatus(ok ? 'signedIn' : 'signedOut'))
      .catch(() => setStatus('signedOut'));
  }, [status]);

  const signIn = useCallback(async (email: string, password: string) => {
    const tokens = await loginRequest(email, password);
    tokenStore.setFromTokenResponse(tokens);
    return tokenStore.getSession()!;
  }, []);

  const changePassword = useCallback(async (currentPassword: string, newPassword: string) => {
    const tokens = await changePasswordRequest(currentPassword, newPassword);
    tokenStore.setFromTokenResponse(tokens);
    return tokenStore.getSession()!;
  }, []);

  const signOut = useCallback(async () => {
    const refreshToken = tokenStore.getRefreshToken();
    try {
      if (refreshToken && tokenStore.getSession()) await logoutRequest(refreshToken);
    } catch {
      // Signing out locally still matters even if the server could not be told.
    } finally {
      tokenStore.clear();
    }
  }, []);

  const value = useMemo(
    () => ({ status, session, signIn, changePassword, signOut }),
    [status, session, signIn, changePassword, signOut],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const context = useContext(AuthContext);
  if (!context) throw new Error('useAuth must be used inside <AuthProvider>.');
  return context;
}
