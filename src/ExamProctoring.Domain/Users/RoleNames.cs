namespace ExamProctoring.Domain.Users;

// The three roles (§2.2). Every user has exactly one, assigned when the account is created.
public static class RoleNames
{
    public const string Admin = "Admin";
    public const string Instructor = "Instructor";
    public const string Student = "Student";

    public static readonly IReadOnlyList<string> All = [Admin, Instructor, Student];
}
