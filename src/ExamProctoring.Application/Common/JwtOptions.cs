namespace ExamProctoring.Application.Common;

// JWT settings (§4.6, §6.6, §6.8), bound from the "Jwt" configuration section.
// SigningKey is a secret: User Secrets in development, the Jwt__SigningKey environment variable elsewhere.
// It never goes in appsettings*.json, and no error message or log line contains it.
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    // HS256 needs a key of at least 256 bits.
    public const int MinSigningKeyBytes = 32;

    public string Issuer { get; set; } = string.Empty;

    public string Audience { get; set; } = string.Empty;

    // Base64 of at least 32 random bytes. The decoded byte length is checked, not the length of the text.
    public string SigningKey { get; set; } = string.Empty;

    public int AccessTokenMinutes { get; set; } = 15;

    public int RefreshTokenDays { get; set; } = 7;

    // Configuration problems, worded so that they never include the key.
    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Issuer))
            errors.Add("Jwt:Issuer is not configured.");
        if (string.IsNullOrWhiteSpace(Audience))
            errors.Add("Jwt:Audience is not configured.");
        if (AccessTokenMinutes <= 0)
            errors.Add("Jwt:AccessTokenMinutes must be greater than 0.");
        if (RefreshTokenDays <= 0)
            errors.Add("Jwt:RefreshTokenDays must be greater than 0.");

        if (string.IsNullOrWhiteSpace(SigningKey))
        {
            errors.Add($"Jwt:SigningKey is not configured. Set it to a Base64 string of at least {MinSigningKeyBytes} random bytes " +
                "(User Secrets in development, the Jwt__SigningKey environment variable elsewhere).");
        }
        else if (!TryDecode(SigningKey, out var key))
        {
            errors.Add("Jwt:SigningKey is not valid Base64.");
        }
        else if (key.Length < MinSigningKeyBytes)
        {
            errors.Add($"Jwt:SigningKey decodes to {key.Length} bytes; at least {MinSigningKeyBytes} are required.");
        }

        return errors;
    }

    public byte[] GetSigningKeyBytes() =>
        TryDecode(SigningKey, out var key) && key.Length >= MinSigningKeyBytes
            ? key
            : throw new InvalidOperationException("Jwt:SigningKey is missing or invalid.");

    private static bool TryDecode(string value, out byte[] key)
    {
        var buffer = new byte[value.Length];
        if (Convert.TryFromBase64String(value, buffer, out var written))
        {
            key = buffer[..written];
            return true;
        }

        key = [];
        return false;
    }
}
