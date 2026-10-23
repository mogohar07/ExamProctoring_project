// Exam times. The backend requires an explicit offset on input and always answers in UTC. The instructor works in the
// browser's local time zone, so every value sent carries that zone's offset; nothing is silently treated as UTC.

const pad = (value: number) => String(value).padStart(2, '0');

function formatOffset(offsetMinutes: number): string {
  const sign = offsetMinutes >= 0 ? '+' : '-';
  const absolute = Math.abs(offsetMinutes);
  return `${sign}${pad(Math.floor(absolute / 60))}:${pad(absolute % 60)}`;
}

// "2026-11-01T09:00" from <input type="datetime-local"> (local wall-clock time) → "2026-11-01T09:00:00+03:00".
// Returns null when the value is empty or not a valid date.
export function localInputToApi(value: string): string | null {
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2})?$/.test(value)) return null;
  const date = new Date(value); // a date-time without an offset is parsed as local time
  if (Number.isNaN(date.getTime())) return null;

  const offset = formatOffset(-date.getTimezoneOffset());
  return (
    `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}` +
    `T${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}${offset}`
  );
}

// An API time ("2026-11-01T06:00:00+00:00") → the local value for <input type="datetime-local">.
export function apiToLocalInput(iso: string): string {
  const date = new Date(iso);
  return (
    `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}` +
    `T${pad(date.getHours())}:${pad(date.getMinutes())}`
  );
}

export function formatDateTime(iso: string | null): string {
  if (!iso) return '—';
  return new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(iso));
}

export function formatDuration(minutes: number): string {
  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;
  if (hours === 0) return `${rest} min`;
  return rest === 0 ? `${hours} h` : `${hours} h ${rest} min`;
}

// For form hints, for example "Africa/Cairo (UTC+03:00)".
export function describeLocalTimeZone(at: Date = new Date()): string {
  const zone = Intl.DateTimeFormat().resolvedOptions().timeZone;
  return `${zone} (UTC${formatOffset(-at.getTimezoneOffset())})`;
}
