namespace ExamProctoring.Application.Auth;

// 201 body for sign-up (§6.4): never a password, hash or token.
public sealed record RegisterStudentResponse(Guid Id, string FullName, string Email, string Role);
