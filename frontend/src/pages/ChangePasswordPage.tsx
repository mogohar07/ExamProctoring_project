import { useState, type FormEvent } from 'react';
import { useNavigate } from 'react-router-dom';
import { ApiError, describeError } from '../api/errors';
import { useAuth } from '../auth/AuthContext';
import { Alert } from '../components/Feedback';
import { FormField, describedBy } from '../components/FormField';

// POST /api/auth/change-password. Required after signing in with a temporary password; every other endpoint answers
// 403 password_change_required until it is done. The server returns a new token pair and ends earlier sessions.
export function ChangePasswordPage() {
  const { session, changePassword, signOut } = useAuth();
  const navigate = useNavigate();
  const [currentPassword, setCurrentPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  const required = session?.mustChangePassword ?? false;

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault();
    setError(null);
    const found: Record<string, string> = {};
    if (!currentPassword) found.currentPassword = 'Current password is required.';
    if (!newPassword) found.newPassword = 'New password is required.';
    // The backend has no confirmation field; this check only protects against typing mistakes.
    if (newPassword !== confirmPassword) found.confirmPassword = 'The passwords do not match.';
    setFieldErrors(found);
    if (Object.keys(found).length > 0) return;

    setSubmitting(true);
    try {
      await changePassword(currentPassword, newPassword);
      navigate('/exams', { replace: true });
    } catch (caught) {
      if (caught instanceof ApiError && caught.status === 400) {
        setFieldErrors(Object.fromEntries(Object.entries(caught.fieldErrors).map(([k, v]) => [k, v.join(' ')])));
      }
      setError(describeError(caught));
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <main className="auth-page">
      <form className="card auth-card form" onSubmit={(event) => void handleSubmit(event)} noValidate>
        <h1>{required ? 'Choose a new password' : 'Change password'}</h1>
        {required && <p className="muted">You signed in with a temporary password. Choose your own password to continue.</p>}
        <p className="muted">At least 8 characters, with an uppercase letter, a lowercase letter and a digit.</p>
        {error && <Alert tone="error">{error}</Alert>}
        <FormField id="cp-current" label="Current (temporary) password" error={fieldErrors.currentPassword}>
          <input
            id="cp-current"
            type="password"
            autoComplete="current-password"
            value={currentPassword}
            onChange={(event) => setCurrentPassword(event.target.value)}
            aria-invalid={Boolean(fieldErrors.currentPassword)}
            aria-describedby={describedBy('cp-current', fieldErrors.currentPassword)}
          />
        </FormField>
        <FormField id="cp-new" label="New password" error={fieldErrors.newPassword}>
          <input
            id="cp-new"
            type="password"
            autoComplete="new-password"
            value={newPassword}
            onChange={(event) => setNewPassword(event.target.value)}
            aria-invalid={Boolean(fieldErrors.newPassword)}
            aria-describedby={describedBy('cp-new', fieldErrors.newPassword)}
          />
        </FormField>
        <FormField id="cp-confirm" label="Repeat the new password" error={fieldErrors.confirmPassword}>
          <input
            id="cp-confirm"
            type="password"
            autoComplete="new-password"
            value={confirmPassword}
            onChange={(event) => setConfirmPassword(event.target.value)}
            aria-invalid={Boolean(fieldErrors.confirmPassword)}
            aria-describedby={describedBy('cp-confirm', fieldErrors.confirmPassword)}
          />
        </FormField>
        <button type="submit" className="button button-block" disabled={submitting}>
          {submitting ? 'Saving…' : 'Change password'}
        </button>
        <button
          type="button"
          className="link-button"
          onClick={() => void signOut().then(() => navigate('/login', { replace: true }))}
        >
          Sign out instead
        </button>
      </form>
    </main>
  );
}
