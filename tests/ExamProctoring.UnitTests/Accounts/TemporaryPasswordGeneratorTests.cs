using ExamProctoring.Application.Accounts;

namespace ExamProctoring.UnitTests.Accounts;

// D18: 16 characters, letters and digits without 0, O, 1, l and I, at least one of each class.
public sealed class TemporaryPasswordGeneratorTests
{
    private static readonly string[] Samples = Enumerable.Range(0, 2000).Select(_ => TemporaryPasswordGenerator.Generate()).ToArray();

    [Fact]
    public void EveryPassword_Has16Characters_FromTheApprovedAlphabet()
    {
        Assert.All(Samples, password =>
        {
            Assert.Equal(16, password.Length);
            Assert.All(password, c => Assert.Contains(c, TemporaryPasswordGenerator.Alphabet));
        });
    }

    [Fact]
    public void TheAlphabet_ExcludesTheLookAlikes_AndHasOnlyLettersAndDigits()
    {
        foreach (var excluded in "0O1lI")
            Assert.DoesNotContain(excluded, TemporaryPasswordGenerator.Alphabet);
        Assert.All(TemporaryPasswordGenerator.Alphabet, c => Assert.True(char.IsAsciiLetterOrDigit(c)));
        Assert.Equal(57, TemporaryPasswordGenerator.Alphabet.Distinct().Count());
    }

    [Fact]
    public void EveryPassword_HasAnUpperCaseLetter_ALowerCaseLetter_AndADigit()
    {
        Assert.All(Samples, password =>
        {
            Assert.Contains(password, char.IsAsciiLetterUpper);
            Assert.Contains(password, char.IsAsciiLetterLower);
            Assert.Contains(password, char.IsAsciiDigit);
        });
    }

    [Fact]
    public void Passwords_AreUnique()
    {
        Assert.Equal(Samples.Length, Samples.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TheGuaranteedCharacters_AreNotAlwaysAtTheStart()
    {
        // Without the shuffle, position 0 would always be upper-case and position 2 always a digit.
        Assert.Contains(Samples, p => !char.IsAsciiLetterUpper(p[0]));
        Assert.Contains(Samples, p => !char.IsAsciiDigit(p[2]));
    }
}
