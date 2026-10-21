using System.Security.Cryptography;

namespace ExamProctoring.Application.Accounts;

// Temporary passwords (D18): 16 characters from a cryptographically secure generator, letters and digits only,
// without the look-alikes 0, O, 1, l and I (they are read aloud or copied by hand), with at least one upper-case
// letter, one lower-case letter and one digit, so they always meet the §6.5 policy. About 92 bits of randomness.
public static class TemporaryPasswordGenerator
{
    public const int Length = 16;
    public const string UpperCase = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    public const string LowerCase = "abcdefghijkmnopqrstuvwxyz";
    public const string Digits = "23456789";
    public const string Alphabet = UpperCase + LowerCase + Digits;

    public static string Generate()
    {
        var characters = new char[Length];
        characters[0] = Pick(UpperCase);
        characters[1] = Pick(LowerCase);
        characters[2] = Pick(Digits);
        for (var i = 3; i < Length; i++)
            characters[i] = Pick(Alphabet);

        // Fisher-Yates shuffle, so the guaranteed characters are not always at the start.
        for (var i = Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (characters[i], characters[j]) = (characters[j], characters[i]);
        }

        return new string(characters);
    }

    private static char Pick(string characterSet) => characterSet[RandomNumberGenerator.GetInt32(characterSet.Length)];
}
