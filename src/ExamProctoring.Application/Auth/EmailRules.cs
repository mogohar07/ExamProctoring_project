using System.Globalization;
using System.Text;

namespace ExamProctoring.Application.Auth;

// The approved email format policy (§2.2, approved 2026-10-20). It checks the shape of an address only;
// it does not prove that the mailbox exists or belongs to the person (email is not verified, §2.5).
public static class EmailRules
{
    public const int MaxLength = 256;

    // After trimming: 1-256 characters, no whitespace, control or invisible format characters, exactly one '@'
    // with text on both sides, and a domain that contains a dot, does not start or end with '.' or '-', and
    // contains no "..". Non-English letters are allowed.
    public static bool IsValid(string? email)
    {
        if (email is null)
            return false;

        var value = email.Trim();
        if (value.Length is < 1 or > MaxLength)
            return false;

        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
                return false;
        }

        // Invisible format characters (Unicode category Cf, approved 2026-10-21), such as U+200B ZERO WIDTH SPACE,
        // would let an address look identical to another one. Checked per code point (Rune) so that format
        // characters outside the Basic Multilingual Plane, such as U+E0001, are caught too.
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format)
                return false;
        }

        var at = value.IndexOf('@');
        if (at <= 0 || at == value.Length - 1 || at != value.LastIndexOf('@'))
            return false;

        var domain = value[(at + 1)..];
        return domain.Contains('.')
            && domain[0] is not ('.' or '-')
            && domain[^1] is not ('.' or '-')
            && !domain.Contains("..");
    }
}
