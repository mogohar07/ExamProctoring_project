using System.Globalization;
using System.Text.RegularExpressions;
using ExamProctoring.Domain.Exams;

namespace ExamProctoring.Application.Exams;

// D25: a start time must carry an explicit time-zone offset, so it is never read as the server's local time.
public static partial class ExamStartTime
{
    public const string FormatExample = "2026-11-01T09:00:00Z or 2026-11-01T11:00:00+02:00";

    // The latest start time whose end time can still be represented, whatever the duration.
    public static readonly DateTimeOffset Latest = DateTimeOffset.MaxValue.AddMinutes(-Exam.MaxDurationMinutes);

    // An ISO 8601 date and time ending in Z or ±hh:mm.
    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}(:[0-9]{2}(\.[0-9]{1,7})?)?(Z|[+-][0-9]{2}:[0-9]{2})$")]
    private static partial Regex WithOffset();

    // The time converted to UTC, or false when the value is missing, malformed or has no offset.
    public static bool TryParse(string? value, out DateTimeOffset utc)
    {
        utc = default;
        if (value is null || !WithOffset().IsMatch(value))
            return false;

        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return false;

        utc = parsed.ToUniversalTime();
        return true;
    }
}
