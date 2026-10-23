import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { tokenResponse } from '../test/fixtures';
import type { Role } from '../types/api';
import { AuthProvider } from './AuthContext';
import { RequireInstructor } from './ProtectedRoute';
import { tokenStore } from './tokenStore';

function renderProtected() {
  return render(
    <AuthProvider>
      <MemoryRouter initialEntries={['/exams']}>
        <Routes>
          <Route path="/login" element={<p>Login page</p>} />
          <Route path="/change-password" element={<p>Change password page</p>} />
          <Route
            path="/exams"
            element={
              <RequireInstructor>
                <p>Exam list</p>
              </RequireInstructor>
            }
          />
        </Routes>
      </MemoryRouter>
    </AuthProvider>,
  );
}

function signIn(role: Role, mustChangePassword = false) {
  tokenStore.setFromTokenResponse(tokenResponse({ role, mustChangePassword }));
}

describe('RequireInstructor', () => {
  it('sends a visitor without a session to the login page', () => {
    renderProtected();
    expect(screen.getByText('Login page')).toBeInTheDocument();
  });

  it('shows the page to a signed-in instructor', () => {
    signIn('Instructor');
    renderProtected();
    expect(screen.getByText('Exam list')).toBeInTheDocument();
  });

  it('sends an account with a pending password change to the change-password page', () => {
    signIn('Instructor', true);
    renderProtected();
    expect(screen.getByText('Change password page')).toBeInTheDocument();
  });

  it.each<Role>(['Student', 'Admin'])('does not show instructor pages to a %s account', (role) => {
    signIn(role);
    renderProtected();
    expect(screen.getByRole('heading', { name: 'Instructor access only' })).toBeInTheDocument();
    expect(screen.queryByText('Exam list')).not.toBeInTheDocument();
  });
});
