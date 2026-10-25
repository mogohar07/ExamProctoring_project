import { useRef, useState, type FormEvent } from 'react';
import { ApiError, describeError } from '../api/errors';
import type { Question, QuestionRequest, QuestionType } from '../types/api';
import { LIMITS, sameOptionText, validateMarks, type FieldErrors } from '../utils/validation';
import { Alert } from './Feedback';
import { FormField, describedBy } from './FormField';

interface OptionDraft {
  key: number;
  text: string;
  isCorrect: boolean;
}

interface QuestionFormProps {
  question?: Question; // edit mode when present
  onSubmit(request: QuestionRequest): Promise<void>;
  onCancel(): void;
}

// One form for create and replace (QuestionRequest). The whole question is sent each time, with all of its options in
// display order; the backend replaces the stored options and gives them new ids.
export function QuestionForm({ question, onSubmit, onCancel }: QuestionFormProps) {
  const nextKey = useRef(0);
  const newOption = (text = '', isCorrect = false): OptionDraft => ({ key: nextKey.current++, text, isCorrect });

  const [type, setType] = useState<QuestionType>(question?.type ?? 'Mcq');
  const [text, setText] = useState(question?.text ?? '');
  // A question saved without marks (Draft only) starts with an empty field: the instructor enters them here.
  const [marks, setMarks] = useState(question ? (question.marks === null ? '' : question.marks.toFixed(2)) : '1');
  const [modelAnswer, setModelAnswer] = useState(question?.modelAnswer ?? '');
  const [options, setOptions] = useState<OptionDraft[]>(() =>
    question && question.options.length > 0
      ? question.options.map((option) => newOption(option.text, option.isCorrect))
      : [newOption('', true), newOption()],
  );
  const [errors, setErrors] = useState<FieldErrors>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  const setOptionText = (key: number, value: string) =>
    setOptions((previous) => previous.map((option) => (option.key === key ? { ...option, text: value } : option)));
  const markCorrect = (key: number) =>
    setOptions((previous) => previous.map((option) => ({ ...option, isCorrect: option.key === key })));
  const removeOption = (key: number) => setOptions((previous) => previous.filter((option) => option.key !== key));

  const validate = (): FieldErrors => {
    const found: FieldErrors = {};
    const trimmed = text.trim();
    if (!trimmed) found.text = 'Text is required.';
    else if (trimmed.length > LIMITS.questionTextMax) found.text = `Text must be at most ${LIMITS.questionTextMax} characters.`;

    const marksError = validateMarks(marks);
    if (marksError) found.marks = marksError;

    if (modelAnswer.trim().length > LIMITS.modelAnswerMax) {
      found.modelAnswer = `Model answer must be at most ${LIMITS.modelAnswerMax} characters.`;
    }

    if (type === 'Mcq') {
      options.forEach((option, index) => {
        const value = option.text.trim();
        if (!value) found[`options[${index}].text`] = 'Option text is required.';
        else if (value.length > LIMITS.optionTextMax) {
          found[`options[${index}].text`] = `Option text must be at most ${LIMITS.optionTextMax} characters.`;
        }
      });
      if (options.length < LIMITS.minMcqOptions) found.options = `An MCQ question needs at least ${LIMITS.minMcqOptions} options.`;
      else if (options.filter((option) => option.isCorrect).length !== 1) found.options = 'Choose exactly one correct option.';
      else {
        const filled = options.map((option) => option.text).filter((value) => value.trim());
        const repeated = filled.some((value, index) => filled.findIndex((other) => sameOptionText(other, value)) !== index);
        if (repeated) found.options = 'Options must not repeat the same text.';
      }
    }
    return found;
  };

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault();
    setFormError(null);
    const clientErrors = validate();
    setErrors(clientErrors);
    if (Object.keys(clientErrors).length > 0) return;

    setSubmitting(true);
    try {
      await onSubmit({
        type,
        text: text.trim(),
        marks: Number(marks.trim()),
        modelAnswer: modelAnswer.trim() ? modelAnswer.trim() : null,
        options: type === 'Mcq' ? options.map((option) => ({ text: option.text.trim(), isCorrect: option.isCorrect })) : [],
      });
    } catch (error) {
      if (error instanceof ApiError && error.status === 400) {
        // Backend names: type, text, marks, modelAnswer, options, options[i].text, options[i] (index from 0).
        setErrors(Object.fromEntries(Object.entries(error.fieldErrors).map(([field, messages]) => [field, messages[0] ?? ''])));
      }
      setFormError(describeError(error));
    } finally {
      setSubmitting(false);
    }
  };

  const idPrefix = question ? `q-${question.id}` : 'q-new';

  return (
    <form className="question-form" onSubmit={(event) => void handleSubmit(event)} noValidate>
      {formError && <Alert tone="error">{formError}</Alert>}

      <div className="form-row">
        <FormField id={`${idPrefix}-type`} label="Type" error={errors.type}>
          <select id={`${idPrefix}-type`} value={type} onChange={(event) => setType(event.target.value as QuestionType)}>
            <option value="Mcq">Multiple choice (MCQ)</option>
            <option value="Essay">Essay</option>
          </select>
        </FormField>
        <FormField id={`${idPrefix}-marks`} label="Marks" error={errors.marks} hint="Greater than 0, at most 2 decimals">
          <input
            id={`${idPrefix}-marks`}
            inputMode="decimal"
            value={marks}
            onChange={(event) => setMarks(event.target.value)}
            aria-invalid={Boolean(errors.marks)}
            aria-describedby={describedBy(`${idPrefix}-marks`, errors.marks, true)}
          />
        </FormField>
      </div>

      <FormField id={`${idPrefix}-text`} label="Question text" error={errors.text}>
        <textarea
          id={`${idPrefix}-text`}
          rows={3}
          value={text}
          onChange={(event) => setText(event.target.value)}
          aria-invalid={Boolean(errors.text)}
          aria-describedby={describedBy(`${idPrefix}-text`, errors.text)}
        />
      </FormField>

      {type === 'Mcq' && (
        <fieldset className="options-editor">
          <legend>Options (select the one correct answer)</legend>
          {options.map((option, index) => {
            const optionError = errors[`options[${index}].text`] ?? errors[`options[${index}]`];
            return (
              <div key={option.key} className={`option-row ${optionError ? 'field-invalid' : ''}`}>
                <input
                  type="radio"
                  name={`${idPrefix}-correct`}
                  checked={option.isCorrect}
                  onChange={() => markCorrect(option.key)}
                  aria-label={`Option ${index + 1} is the correct answer`}
                />
                <input
                  className="option-text"
                  value={option.text}
                  placeholder={`Option ${index + 1}`}
                  onChange={(event) => setOptionText(option.key, event.target.value)}
                  aria-label={`Option ${index + 1} text`}
                  aria-invalid={Boolean(optionError)}
                />
                <button
                  type="button"
                  className="button button-secondary button-small"
                  onClick={() => removeOption(option.key)}
                  disabled={options.length <= LIMITS.minMcqOptions}
                  aria-label={`Remove option ${index + 1}`}
                >
                  Remove
                </button>
                {optionError && <p className="field-error option-error">{optionError}</p>}
              </div>
            );
          })}
          {errors.options && <p className="field-error">{errors.options}</p>}
          <button type="button" className="button button-secondary button-small" onClick={() => setOptions((p) => [...p, newOption()])}>
            Add option
          </button>
          <p className="field-hint">
            At least {LIMITS.minMcqOptions} options. The maximum is a server setting; the server reports it if you go over.
          </p>
        </fieldset>
      )}

      <FormField
        id={`${idPrefix}-model`}
        label="Model answer (optional)"
        error={errors.modelAnswer}
        hint="Visible to you only. Students never see it."
      >
        <textarea
          id={`${idPrefix}-model`}
          rows={2}
          value={modelAnswer}
          onChange={(event) => setModelAnswer(event.target.value)}
          aria-invalid={Boolean(errors.modelAnswer)}
          aria-describedby={describedBy(`${idPrefix}-model`, errors.modelAnswer, true)}
        />
      </FormField>

      <div className="form-actions">
        <button type="button" className="button button-secondary" onClick={onCancel} disabled={submitting}>
          Cancel
        </button>
        <button type="submit" className="button" disabled={submitting}>
          {submitting ? 'Saving…' : question ? 'Save question' : 'Add question'}
        </button>
      </div>
    </form>
  );
}
