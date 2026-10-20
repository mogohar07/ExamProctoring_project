namespace ExamProctoring.Application.Common;

// The claims an access token carries (§6.6). Inbound claim mapping is off, so these names are used as they are.
public static class AuthClaimTypes
{
    public const string Subject = "sub";
    public const string Email = "email";
    public const string Name = "name";
    public const string Role = "role";
    public const string SecurityStamp = "sstamp";
    public const string TokenId = "jti";
}
