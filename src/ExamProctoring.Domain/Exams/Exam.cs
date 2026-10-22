namespace ExamProctoring.Domain.Exams;

// An exam and its schedule (§5.2), owned by one instructor (§2.2). Only Draft exams may be hard-deleted; published
// exams never are (§5.1).
public class Exam
{
    // The entity's starting value (R4). Exams created through the API take the configured warning limit instead (D26).
    public const int DefaultWarningLimit = 3;

    // D25: an exam lasts at most 6 hours.
    public const int MaxDurationMinutes = 360;

    public Guid Id { get; set; }

    // The owning instructor.
    public Guid InstructorId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public DateTimeOffset StartsAt { get; set; }

    public int DurationMinutes { get; set; }

    public ExamStatus Status { get; set; } = ExamStatus.Draft;

    public int WarningLimit { get; set; } = DefaultWarningLimit;

    public DateTimeOffset? PublishedAt { get; set; }

    public DateTimeOffset? ResultsPublishedAt { get; set; }

    public DateTimeOffset? AnalysisCompletedAt { get; set; }

    // SQL Server rowversion: changes on every update of the exam row and guards against stale updates (§5.1).
    public byte[] RowVersion { get; set; } = [];

    // Fixed window (§2.2). Calculated, not stored.
    public DateTimeOffset EndsAt => StartsAt.AddMinutes(DurationMinutes);

    // D28: Upcoming before StartsAt, Open from StartsAt until EndsAt, Closed from EndsAt on. Calculated on read for
    // every status; it is not the exam's status.
    public ExamPhase PhaseAt(DateTimeOffset now) =>
        now < StartsAt ? ExamPhase.Upcoming
        : now < EndsAt ? ExamPhase.Open
        : ExamPhase.Closed;
}
