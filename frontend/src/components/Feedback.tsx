import type { ReactNode } from 'react';

export function Spinner({ label = 'Loading…' }: { label?: string }) {
  return (
    <span className="spinner-wrap" role="status" aria-live="polite">
      <span className="spinner" aria-hidden="true" />
      <span>{label}</span>
    </span>
  );
}

export function PageLoader({ label }: { label?: string }) {
  return (
    <div className="page-loader">
      <Spinner label={label} />
    </div>
  );
}

type AlertTone = 'error' | 'success' | 'info' | 'warning';

export function Alert({ tone, children, onDismiss }: { tone: AlertTone; children: ReactNode; onDismiss?: () => void }) {
  return (
    <div className={`alert alert-${tone}`} role={tone === 'error' ? 'alert' : 'status'}>
      <div className="alert-body">{children}</div>
      {onDismiss && (
        <button type="button" className="alert-dismiss" onClick={onDismiss} aria-label="Dismiss message">
          ×
        </button>
      )}
    </div>
  );
}

export function EmptyState({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <div className="empty-state">
      <h3>{title}</h3>
      {children}
    </div>
  );
}
