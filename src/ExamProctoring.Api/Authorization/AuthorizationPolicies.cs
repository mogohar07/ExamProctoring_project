namespace ExamProctoring.Api.Authorization;

// Role policies (§6.1 check 3, §6.7), applied at controller level. Each user has exactly one role (§2.2).
public static class AuthorizationPolicies
{
    public const string AdminOnly = "AdminOnly";
    public const string InstructorOnly = "InstructorOnly";
    public const string StudentOnly = "StudentOnly";
}
