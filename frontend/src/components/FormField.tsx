import type { ReactNode } from 'react';

interface FormFieldProps {
  id: string;
  label: string;
  error?: string;
  hint?: ReactNode;
  children: ReactNode;
}

// Label, control, hint and error message, linked for screen readers through the control's id.
export function FormField({ id, label, error, hint, children }: FormFieldProps) {
  return (
    <div className={`field ${error ? 'field-invalid' : ''}`}>
      <label htmlFor={id}>{label}</label>
      {children}
      {hint && !error && (
        <p className="field-hint" id={`${id}-hint`}>
          {hint}
        </p>
      )}
      {error && (
        <p className="field-error" id={`${id}-error`}>
          {error}
        </p>
      )}
    </div>
  );
}

// aria-describedby value for the control inside a FormField.
export function describedBy(id: string, error?: string, hasHint = false): string | undefined {
  if (error) return `${id}-error`;
  return hasHint ? `${id}-hint` : undefined;
}
