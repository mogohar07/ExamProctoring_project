namespace ExamProctoring.Application.Accounts;

// POST /api/admin/instructors (§6.4): exactly these two fields. There is no role field, so an extra JSON property
// such as "role" is never bound and the account is always an Instructor.
// Nullable so that a missing field is reported as validation_failed.
public sealed record CreateInstructorRequest(string? FullName, string? Email);
