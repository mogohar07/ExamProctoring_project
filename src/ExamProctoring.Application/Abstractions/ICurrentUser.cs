namespace ExamProctoring.Application.Abstractions;

// The caller (§6.7): read from the access token's claims, and the only way services learn who is calling.
// Only valid on authenticated requests; reading it on an anonymous request throws.
public interface ICurrentUser
{
    Guid UserId { get; }

    string Role { get; }

    // The security stamp the access token was issued with; the account gate has checked it matched the account.
    string SecurityStamp { get; }
}
