import type { ReactNode } from 'react';
import { Navigate, useLocation } from 'react-router-dom';
import { PageLoader } from '../components/Feedback';
import { useAuth } from './AuthContext';

// Pages for signed-in instructors. This only shapes the UI: the backend enforces the Instructor role, account status
// and ownership on every request, whatever the frontend shows.
export function RequireInstructor({ children }: { children: ReactNode }) {
  const { status, session, signOut } = useAuth();
  const location = useLocation();

  if (status === 'loading') return <PageLoader label="Restoring your session…" />;

  if (!session) {
    return <Navigate to="/login" replace state={{ from: location.pathname + location.search }} />;
  }

  if (session.mustChangePassword) return <Navigate to="/change-password" replace />;

  if (session.user.role !== 'Instructor') {
    return (
      <main className="auth-page">
        <div className="card auth-card">
          <h1>Instructor access only</h1>
          <p>
            You are signed in as <strong>{session.user.email}</strong> ({session.user.role}). This dashboard manages exams
            and is available to instructor accounts only.
          </p>
          <button type="button" className="button" onClick={() => void signOut()}>
            Sign out
          </button>
        </div>
      </main>
    );
  }

  return <>{children}</>;
}

// Pages that need any signed-in account (the password change screen).
export function RequireSession({ children }: { children: ReactNode }) {
  const { status, session } = useAuth();
  if (status === 'loading') return <PageLoader label="Restoring your session…" />;
  if (!session) return <Navigate to="/login" replace />;
  return <>{children}</>;
}
