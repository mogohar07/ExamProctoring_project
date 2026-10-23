import { Link, NavLink, Outlet, useNavigate } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';

export function Layout() {
  const { session, signOut } = useAuth();
  const navigate = useNavigate();

  const handleSignOut = async () => {
    await signOut();
    navigate('/login', { replace: true });
  };

  return (
    <div className="app-shell">
      <header className="app-header">
        <Link to="/exams" className="brand">
          Exam Proctoring <span className="brand-sub">Instructor</span>
        </Link>
        <nav className="app-nav" aria-label="Main">
          <NavLink to="/exams" end>
            Exams
          </NavLink>
          <NavLink to="/exams/new">New exam</NavLink>
        </nav>
        <div className="app-user">
          <span className="app-user-name" title={session?.user.email}>
            {session?.user.fullName}
          </span>
          <button type="button" className="button button-secondary button-small" onClick={() => void handleSignOut()}>
            Sign out
          </button>
        </div>
      </header>
      <main className="app-main">
        <Outlet />
      </main>
    </div>
  );
}
