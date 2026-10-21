namespace ExamProctoring.Domain.Exams;

// Stored as a string (§5.1). A new exam is a Draft; publishing is Step 2.4.
public enum ExamStatus
{
    Draft,
    Published,
    Cancelled
}
