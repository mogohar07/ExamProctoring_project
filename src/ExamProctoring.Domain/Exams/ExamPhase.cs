namespace ExamProctoring.Domain.Exams;

// Where an exam stands in time (D28). Calculated from StartsAt and EndsAt on read; never stored.
public enum ExamPhase
{
    Upcoming,
    Open,
    Closed
}
