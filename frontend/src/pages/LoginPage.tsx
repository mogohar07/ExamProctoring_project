import { useState, type FormEvent } from 'react';
import { Navigate, useLocation, useNavigate } from 'react-router-dom';
import { ApiError, describeError } from '../api/errors';
import { useAuth } from '../auth/AuthContext';
import { Alert } from '../components/Feedback';
import { FormField, describedBy } from '../components/FormField';

// POST /api/auth/login. Instructor accounts are created by an admin with a temporary password, which must be changed
// at first sign-in (the backend then answers mustChangePassword: true).
export function LoginPage() {
  const { session, signIn } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const from = (location.state as { from?: string } | null)?.from ?? '/exams';

  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  if (session) return <Navigate to={session.mustChangePassword ? '/change-password' : from} replace />;

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault();
    setError(null);
    const missing: Record<string, string> = {};
    if (!email.trim()) missing.email = 'Email is required.';
    if (!password) missing.password = 'Password is required.';
    setFieldErrors(missing);
    if (Object.keys(missing).length > 0) return;

    setSubmitting(true);
    try {
      const signedIn = await signIn(email.trim(), password);
      navigate(signedIn.mustChangePassword ? '/change-password' : from, { replace: true });
    } catch (caught) {
      if (caught instanceof ApiError && caught.status === 400) {
        setFieldErrors(Object.fromEntries(Object.entries(caught.fieldErrors).map(([k, v]) => [k, v[0] ?? ''])));
      }
      // invalid_credentials, account_locked, account_deactivated and temporary_password_expired carry a readable
      // detail message from the backend.
      setError(describeError(caught));
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <main className="auth-page">
      <form className="card auth-card form" onSubmit={(event) => void handleSubmit(event)} noValidate>
        <h1>Sign in</h1>
        <p className="muted">Exam management for instructors.</p>
        {error && <Alert tone="error">{error}</Alert>}
        <FormField id="login-email" label="Email" error={fieldErrors.email}>
          <input
            id="login-email"
            type="email"
            autoComplete="username"
            value={email}
            onChange={(event) => setEmail(event.target.value)}
            aria-invalid={Boolean(fieldErrors.email)}
            aria-describedby={describedBy('login-email', fieldErrors.email)}
          />
        </FormField>
        <FormField id="login-password" label="Password" error={fieldErrors.password}>
          <input
            id="login-password"
            type="password"
            autoComplete="current-password"
            value={password}
            onChange={(event) => setPassword(event.target.value)}
            aria-invalid={Boolean(fieldErrors.password)}
            aria-describedby={describedBy('login-password', fieldErrors.password)}
          />
        </FormField>
        <button type="submit" className="button button-block" disabled={submitting}>
          {submitting ? 'Signing in…' : 'Sign in'}
        </button>
      </form>
    </main>
  );
}
