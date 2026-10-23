import { describe, expect, it } from 'vitest';
import { apiToLocalInput, formatDuration, localInputToApi } from './dates';
import { validateMarks } from './validation';

function expectedOffset(localValue: string): string {
  const minutes = -new Date(localValue).getTimezoneOffset();
  const sign = minutes >= 0 ? '+' : '-';
  const absolute = Math.abs(minutes);
  return `${sign}${String(Math.floor(absolute / 60)).padStart(2, '0')}:${String(absolute % 60).padStart(2, '0')}`;
}

describe('localInputToApi', () => {
  it('keeps the local wall-clock time and adds the local offset instead of assuming UTC', () => {
    const value = '2030-06-15T09:30';

    const result = localInputToApi(value);

    expect(result).toBe(`2030-06-15T09:30:00${expectedOffset(value)}`);
    expect(Date.parse(result!)).toBe(new Date(value).getTime());
  });

  it('matches the backend format: date, time and an explicit offset', () => {
    expect(localInputToApi('2030-01-01T00:00')).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}[+-]\d{2}:\d{2}$/);
  });

  it('rejects empty or malformed values', () => {
    expect(localInputToApi('')).toBeNull();
    expect(localInputToApi('tomorrow')).toBeNull();
    expect(localInputToApi('2030-13-45T99:99')).toBeNull();
  });

  it('round-trips an API time through the local input format', () => {
    const local = apiToLocalInput('2030-06-15T06:30:00+00:00');
    expect(Date.parse(localInputToApi(local)!)).toBe(Date.parse('2030-06-15T06:30:00+00:00'));
  });
});

describe('formatDuration', () => {
  it('formats minutes', () => {
    expect(formatDuration(45)).toBe('45 min');
    expect(formatDuration(120)).toBe('2 h');
    expect(formatDuration(90)).toBe('1 h 30 min');
  });
});

describe('validateMarks (mirrors the backend rule)', () => {
  it.each(['0.01', '1', '2.5', '1.500', '9999.99'])('accepts %s', (value) => {
    expect(validateMarks(value)).toBeNull();
  });

  it.each([
    ['0', 'Marks must be greater than 0.'],
    ['10000', 'Marks must be at most 9999.99.'],
    ['1.005', 'Marks can have at most 2 decimal places.'],
    ['', 'Marks are required.'],
    ['abc', 'Marks must be a number.'],
  ])('rejects %s', (value, message) => {
    expect(validateMarks(value)).toBe(message);
  });
});
