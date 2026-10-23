import { BrowserRouter, Link, Navigate, Route, Routes } from 'react-router-dom';
import { AuthProvider } from './auth/AuthContext';
import { RequireInstructor, RequireSession } from './auth/ProtectedRoute';
import { Layout } from './components/Layout';
import { ChangePasswordPage } from './pages/ChangePasswordPage';
import { CreateExamPage, EditExamPage } from './pages/ExamEditorPages';
import { ExamDetailsPage } from './pages/ExamDetailsPage';
import { ExamListPage } from './pages/ExamListPage';
import { LoginPage } from './pages/LoginPage';

export function AppRoutes() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route
        path="/change-password"
        element={
          <RequireSession>
            <ChangePasswordPage />
          </RequireSession>
        }
      />
      <Route
        element={
          <RequireInstructor>
            <Layout />
          </RequireInstructor>
        }
      >
        <Route path="/exams" element={<ExamListPage />} />
        <Route path="/exams/new" element={<CreateExamPage />} />
        <Route path="/exams/:examId" element={<ExamDetailsPage />} />
        <Route path="/exams/:examId/edit" element={<EditExamPage />} />
      </Route>
      <Route path="/" element={<Navigate to="/exams" replace />} />
      <Route
        path="*"
        element={
          <main className="auth-page">
            <div className="card auth-card">
              <h1>Page not found</h1>
              <Link to="/exams">Go to my exams</Link>
            </div>
          </main>
        }
      />
    </Routes>
  );
}

export function App() {
  return (
    <AuthProvider>
      <BrowserRouter>
        <AppRoutes />
      </BrowserRouter>
    </AuthProvider>
  );
}
