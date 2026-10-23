import { useState, type FormEvent } from 'react';
import { ApiError, describeError } from '../api/errors';
import type { Exam, ExamRequest } from '../types/api';
import { apiToLocalInput, describeLocalTimeZone, localInputToApi } from '../utils/dates';
import { LIMITS, validateExamForm, type ExamFormValues, type FieldErrors } from '../utils/validation';
import { Alert } from './Feedback';
import { FormField, describedBy } from './FormField';

interface ExamFormProps {
  exam?: Exam; // edit mode when present
  submitLabel: string;
  onSubmit(request: ExamRequest): Promise<void>;
  onCancel(): void;
}

// Create and edit share the same request (ExamRequest): title, description, start time and duration. The warning
// limit, status and publication time are set by the system and are not editable here.
export function ExamForm({ exam, submitLabel, onSubmit, onCancel }: ExamFormProps) {
  const [values, setValues] = useState<ExamFormValues>(() => ({
    title: exam?.title ?? '',
    description: exam?.description ?? '',
    startsAtLocal: exam ? apiToLocalInput(exam.startsAt) : '',
    durationMinutes: exam ? String(exam.durationMinutes) : '60',
  }));
  const [errors, setErrors] = useState<FieldErrors>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  // On edit, an untouched start time is sent back exactly as the server returned it, so it doesn't count as a change.
  const startTimeChanged = !exam || values.startsAtLocal !== apiToLocalInput(exam.startsAt);

  const update = (field: keyof ExamFormValues) => (value: string) => {
    setValues((previous) => ({ ...previous, [field]: value }));
    setErrors((previous) => ({ ...previous, [field === 'startsAtLocal' ? 'startsAt' : field]: '' }));
  };

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault();
    setFormError(null);
    const clientErrors = validateExamForm(values, { startTimeChanged });
    setErrors(clientErrors);
    if (Object.keys(clientErrors).length > 0) return;

    const startsAt = startTimeChanged ? localInputToApi(values.startsAtLocal)! : exam!.startsAt;
    const description = values.description.trim();
    setSubmitting(true);
    try {
      await onSubmit({
        title: values.title.trim(),
        description: description ? description : null,
        startsAt,
        durationMinutes: Number(values.durationMinutes),
      });
    } catch (error) {
      if (error instanceof ApiError && error.status === 400 && Object.keys(error.fieldErrors).length > 0) {
        setErrors(firstMessages(error.fieldErrors));
      }
      setFormError(describeError(error));
    } finally {
      setSubmitting(false);
    }
  };

  const zone = describeLocalTimeZone();

  return (
    <form className="card form" onSubmit={(event) => void handleSubmit(event)} noValidate>
      {formError && <Alert tone="error">{formError}</Alert>}

      <FormField id="exam-title" label="Title" error={errors.title}>
        <input
          id="exam-title"
          value={values.title}
          maxLength={LIMITS.titleMax + 20}
          onChange={(event) => update('title')(event.target.value)}
          aria-invalid={Boolean(errors.title)}
          aria-describedby={describedBy('exam-title', errors.title)}
          required
        />
      </FormField>

      <FormField
        id="exam-description"
        label="Description (optional)"
        error={errors.description}
        hint={`${values.description.trim().length}/${LIMITS.descriptionMax} characters`}
      >
        <textarea
          id="exam-description"
          rows={4}
          value={values.description}
          onChange={(event) => update('description')(event.target.value)}
          aria-invalid={Boolean(errors.description)}
          aria-describedby={describedBy('exam-description', errors.description, true)}
        />
      </FormField>

      <div className="form-row">
        <FormField
          id="exam-starts-at"
          label="Start date and time"
          error={errors.startsAt}
          hint={`Your local time: ${zone}. It is sent with this offset and stored in UTC.`}
        >
          <input
            id="exam-starts-at"
            type="datetime-local"
            value={values.startsAtLocal}
            onChange={(event) => update('startsAtLocal')(event.target.value)}
            aria-invalid={Boolean(errors.startsAt)}
            aria-describedby={describedBy('exam-starts-at', errors.startsAt, true)}
            required
          />
        </FormField>

        <FormField
          id="exam-duration"
          label="Duration (minutes)"
          error={errors.durationMinutes}
          hint={`${LIMITS.durationMin}–${LIMITS.durationMax} minutes`}
        >
          <input
            id="exam-duration"
            type="number"
            inputMode="numeric"
            min={LIMITS.durationMin}
            max={LIMITS.durationMax}
            step={1}
            value={values.durationMinutes}
            onChange={(event) => update('durationMinutes')(event.target.value)}
            aria-invalid={Boolean(errors.durationMinutes)}
            aria-describedby={describedBy('exam-duration', errors.durationMinutes, true)}
            required
          />
        </FormField>
      </div>

      {exam && (
        <p className="muted">
          Warning limit: {exam.warningLimit} (set by the system; it can't be changed here).
        </p>
      )}

      <div className="form-actions">
        <button type="button" className="button button-secondary" onClick={onCancel} disabled={submitting}>
          Cancel
        </button>
        <button type="submit" className="button" disabled={submitting}>
          {submitting ? 'Saving…' : submitLabel}
        </button>
      </div>
    </form>
  );
}

// Backend field names are camelCase JSON names (title, description, startsAt, durationMinutes).
function firstMessages(fieldErrors: Record<string, string[]>): FieldErrors {
  return Object.fromEntries(Object.entries(fieldErrors).map(([field, messages]) => [field, messages[0] ?? '']));
}
