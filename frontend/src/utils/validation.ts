import { localInputToApi } from './dates';

// Early checks that mirror the backend validators (ExamRequestValidator, QuestionRequestValidator) so instructors see
// mistakes before submitting. The backend stays the authority: its 400 field errors are shown as well.
// Limits come from Domain/Exams/ExamFieldLimits.cs, Exam.cs and QuestionRules.cs.

export const LIMITS = {
  titleMax: 200,
  descriptionMax: 2000,
  durationMin: 1,
  durationMax: 360,
  questionTextMax: 4000,
  modelAnswerMax: 4000,
  optionTextMax: 1000,
  minMcqOptions: 2,
  maxMarks: 9999.99,
} as const;

export type FieldErrors = Record<string, string>;

export interface ExamFormValues {
  title: string;
  description: string;
  startsAtLocal: string; // datetime-local value
  durationMinutes: string;
}

export function validateExamForm(values: ExamFormValues, options: { startTimeChanged: boolean }): FieldErrors {
  const errors: FieldErrors = {};
  const title = values.title.trim();
  if (!title) errors.title = 'Title is required.';
  else if (title.length > LIMITS.titleMax) errors.title = `Title must be at most ${LIMITS.titleMax} characters.`;

  if (values.description.trim().length > LIMITS.descriptionMax) {
    errors.description = `Description must be at most ${LIMITS.descriptionMax} characters.`;
  }

  if (!values.startsAtLocal) {
    errors.startsAt = 'Start time is required.';
  } else if (options.startTimeChanged) {
    const iso = localInputToApi(values.startsAtLocal);
    if (!iso) errors.startsAt = 'Enter a valid date and time.';
    else if (Date.parse(iso) <= Date.now()) errors.startsAt = 'Start time must be in the future.';
  }

  const duration = Number(values.durationMinutes);
  if (!values.durationMinutes.trim()) errors.durationMinutes = 'Duration is required.';
  else if (!Number.isInteger(duration) || duration < LIMITS.durationMin || duration > LIMITS.durationMax) {
    errors.durationMinutes = `Duration must be between ${LIMITS.durationMin} and ${LIMITS.durationMax} minutes.`;
  }

  return errors;
}

// Marks: greater than 0, at most 9999.99, at most two decimal places.
export function validateMarks(value: string): string | null {
  const trimmed = value.trim();
  if (!trimmed) return 'Marks are required.';
  if (!/^\d+(\.\d+)?$/.test(trimmed)) return 'Marks must be a number.';
  const marks = Number(trimmed);
  if (marks <= 0) return 'Marks must be greater than 0.';
  if (marks > LIMITS.maxMarks) return `Marks must be at most ${LIMITS.maxMarks}.`;
  if (!/^\d+(\.\d{1,2})?0*$/.test(trimmed)) return 'Marks can have at most 2 decimal places.';
  return null;
}

// Text comparison used by the backend for repeated options: trimmed, ignoring letter case.
export function sameOptionText(a: string, b: string): boolean {
  return a.trim().toLowerCase() === b.trim().toLowerCase();
}
