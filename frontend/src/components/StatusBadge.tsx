import type { ExamPhase, ExamStatus } from '../types/api';

const PHASE_LABELS: Record<ExamPhase, string> = {
  Upcoming: 'Upcoming',
  Open: 'In progress',
  Closed: 'Ended',
};

export function StatusBadge({ status }: { status: ExamStatus }) {
  return <span className={`badge badge-${status.toLowerCase()}`}>{status}</span>;
}

// The phase is calculated by the backend from the start and end times when the exam was loaded.
export function PhaseBadge({ phase }: { phase: ExamPhase }) {
  return <span className={`badge badge-phase-${phase.toLowerCase()}`}>{PHASE_LABELS[phase]}</span>;
}
