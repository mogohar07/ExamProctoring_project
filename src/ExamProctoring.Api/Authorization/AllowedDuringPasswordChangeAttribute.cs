namespace ExamProctoring.Api.Authorization;

// Marks the endpoints that still work while MustChangePassword is set: me, logout and change-password (§6.3).
// Every other protected endpoint answers 403 password_change_required until the password is changed.
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class AllowedDuringPasswordChangeAttribute : Attribute;
