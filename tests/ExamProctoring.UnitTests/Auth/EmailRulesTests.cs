using ExamProctoring.Application.Auth;

namespace ExamProctoring.UnitTests.Auth;

// The approved email policy (§2.2, approved 2026-10-20).
public sealed class EmailRulesTests
{
    [Theory]
    [InlineData("a@b.co")]
    [InlineData("324341896@sha.edu.eg")]
    [InlineData("name@gmail.com")]
    [InlineData("o'brien@university.edu")]
    [InlineData("user+tag@example.com")]
    [InlineData("josé.garcía@universidad.es")]
    [InlineData("محمد@example.com")]
    [InlineData("user@sub.domain.example")]
    [InlineData("  padded@example.com  ")]   // surrounding whitespace is trimmed first
    public void ValidAddresses_AreAccepted(string email) => Assert.True(EmailRules.IsValid(email));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("plainaddress")]
    [InlineData("@example.com")]
    [InlineData("user@")]
    [InlineData("a@b@example.com")]
    [InlineData("a b@example.com")]
    [InlineData("user@exam ple.com")]
    [InlineData("tab\t@example.com")]
    [InlineData("nbsp @example.com")]
    [InlineData("ctrl\u0001@example.com")]
    [InlineData("user@example")]
    [InlineData("user@.example.com")]
    [InlineData("user@example.com.")]
    [InlineData("user@-example.com")]
    [InlineData("user@example.com-")]
    [InlineData("user@exa..mple.com")]
    public void InvalidAddresses_AreRejected(string email) => Assert.False(EmailRules.IsValid(email));

    [Fact]
    public void Null_IsRejected() => Assert.False(EmailRules.IsValid(null));

    // Invisible format characters (Unicode category Cf), approved for rejection on 2026-10-21.
    [Theory]
    [InlineData(0x200B)]  // ZERO WIDTH SPACE
    [InlineData(0x200C)]  // ZERO WIDTH NON-JOINER
    [InlineData(0x200D)]  // ZERO WIDTH JOINER
    [InlineData(0x2060)]  // WORD JOINER
    [InlineData(0xFEFF)]  // ZERO WIDTH NO-BREAK SPACE (byte order mark)
    [InlineData(0x00AD)]  // SOFT HYPHEN
    [InlineData(0x200E)]  // LEFT-TO-RIGHT MARK
    [InlineData(0x202E)]  // RIGHT-TO-LEFT OVERRIDE
    [InlineData(0xE0001)] // LANGUAGE TAG (outside the Basic Multilingual Plane)
    public void InvisibleFormatCharacters_AreRejected_Anywhere(int codePoint)
    {
        var c = char.ConvertFromUtf32(codePoint);

        Assert.False(EmailRules.IsValid($"sa{c}ra@example.com"));
        Assert.False(EmailRules.IsValid($"sara@exa{c}mple.com"));
        Assert.False(EmailRules.IsValid($"{c}sara@example.com"));   // not removed by trimming
        Assert.False(EmailRules.IsValid($"sara@example.com{c}"));
    }

    [Fact]
    public void ZeroWidthSpace_CannotMakeALookAlikeOfAValidAddress()
    {
        Assert.True(EmailRules.IsValid("sara@example.com"));
        Assert.False(EmailRules.IsValid("sara​@example.com"));
    }

    [Theory]
    [InlineData("josé.garcía@universidad.es")]
    [InlineData("müller@straße.de")]
    [InlineData("محمد@example.com")]
    [InlineData("محمد@مثال.مصر")]
    [InlineData("用户@例子.广告")]
    [InlineData("иван@пример.рф")]
    [InlineData("δοκιμή@παράδειγμα.δοκιμή")]
    [InlineData("उपयोगकर्ता@उदाहरण.कॉम")]   // Devanagari vowel signs and virama are combining marks, not format characters
    public void InternationalAddresses_AreStillAccepted(string email) => Assert.True(EmailRules.IsValid(email));

    [Fact]
    public void Length_IsLimitedTo256CharactersAfterTrimming()
    {
        const string domain = "@example.com";
        var exactly256 = new string('a', 256 - domain.Length) + domain;
        var length257 = new string('a', 257 - domain.Length) + domain;

        Assert.True(EmailRules.IsValid(exactly256));
        Assert.True(EmailRules.IsValid($"  {exactly256}  "));
        Assert.False(EmailRules.IsValid(length257));
    }
}
