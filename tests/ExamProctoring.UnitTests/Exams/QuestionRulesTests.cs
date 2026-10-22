using ExamProctoring.Domain.Exams;

namespace ExamProctoring.UnitTests.Exams;

// An MCQ has at least two options with distinct texts and exactly one correct option (§2.2, §5.3, D32); an Essay has
// no options (§5.2, confirmed 2026-10-21); marks are greater than 0, at most 9999.99, with two decimals at most (D31, D35).
public sealed class QuestionRulesTests
{
    [Theory]
    [InlineData(new[] { true, false })]
    [InlineData(new[] { false, true, false, false })]
    public void Mcq_WithExactlyOneCorrectOption_IsValid(bool[] correct)
    {
        var options = Options(correct);

        Assert.True(QuestionRules.HasExactlyOneCorrectOption(options));
        Assert.True(QuestionRules.OptionsAreValid(QuestionType.Mcq, options));
    }

    [Theory]
    [InlineData(new bool[0])]
    [InlineData(new[] { false })]
    [InlineData(new[] { false, false })]
    [InlineData(new[] { true, true })]
    [InlineData(new[] { true, false, true })]
    public void Mcq_WithoutExactlyOneCorrectOption_IsInvalid(bool[] correct)
    {
        var options = Options(correct);

        Assert.False(QuestionRules.HasExactlyOneCorrectOption(options));
        Assert.False(QuestionRules.OptionsAreValid(QuestionType.Mcq, options));
    }

    // D32: one option is not enough, even when it is the correct one.
    [Fact]
    public void Mcq_WithASingleOption_IsInvalid()
    {
        var options = Options([true]);

        Assert.True(QuestionRules.HasExactlyOneCorrectOption(options));
        Assert.False(QuestionRules.HasEnoughOptions(options));
        Assert.False(QuestionRules.OptionsAreValid(QuestionType.Mcq, options));
    }

    [Fact]
    public void MinMcqOptions_Is2() =>
        Assert.Equal(2, QuestionRules.MinMcqOptions);

    [Theory]
    [InlineData(2, 2, true)]
    [InlineData(8, 8, true)]
    [InlineData(9, 8, false)]
    [InlineData(4, 3, false)]
    public void HasAtMostOptions_ComparesWithTheGivenMaximum(int count, int max, bool expected) =>
        Assert.Equal(expected, QuestionRules.HasAtMostOptions(Options([true, .. Enumerable.Repeat(false, count - 1)]), max));

    // D32: compared after trimming, ignoring letter case, ordinal.
    [Theory]
    [InlineData("Paris", "paris")]
    [InlineData("Paris", " PARIS ")]
    [InlineData("A", "a")]
    public void Mcq_WithRepeatedOptionTexts_IsInvalid(string first, string second)
    {
        var options = new[]
        {
            new QuestionOption { Text = first, IsCorrect = true, Position = 1 },
            new QuestionOption { Text = second, IsCorrect = false, Position = 2 },
        };

        Assert.False(QuestionRules.HasDistinctOptionTexts(options));
        Assert.False(QuestionRules.OptionsAreValid(QuestionType.Mcq, options));
    }

    // No Unicode normalization: these look alike but are different texts.
    [Theory]
    [InlineData("Paris", "Pariss")]
    [InlineData("caf\u00e9", "cafe\u0301")]
    [InlineData("1", "1.0")]
    public void Mcq_WithDifferentOptionTexts_IsValid(string first, string second)
    {
        var options = new[]
        {
            new QuestionOption { Text = first, IsCorrect = true, Position = 1 },
            new QuestionOption { Text = second, IsCorrect = false, Position = 2 },
        };

        Assert.True(QuestionRules.HasDistinctOptionTexts(options));
        Assert.True(QuestionRules.OptionsAreValid(QuestionType.Mcq, options));
    }

    [Theory]
    [InlineData("0.01")]
    [InlineData("1")]
    [InlineData("1.5")]
    [InlineData("1.50")]
    [InlineData("1.500")]
    [InlineData("9999.99")]
    public void Marks_GreaterThan0_AtMost9999_99_WithTwoDecimals_AreValid(string marks) =>
        Assert.True(QuestionRules.MarksAreValid(decimal.Parse(marks, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("0.001")]
    [InlineData("1.005")]
    [InlineData("10000")]
    [InlineData("9999.991")]
    public void Marks_OutsideTheRules_AreInvalid(string marks) =>
        Assert.False(QuestionRules.MarksAreValid(decimal.Parse(marks, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void MaxMarks_Is9999_99() =>
        Assert.Equal(9999.99m, QuestionRules.MaxMarks);

    [Fact]
    public void TrueFalse_IsATwoOptionMcq_WithOneCorrectOption()
    {
        var options = new[]
        {
            new QuestionOption { Text = "True", IsCorrect = false, Position = 1 },
            new QuestionOption { Text = "False", IsCorrect = true, Position = 2 },
        };

        Assert.True(QuestionRules.OptionsAreValid(QuestionType.Mcq, options));
    }

    [Fact]
    public void Essay_WithNoOptions_IsValid()
    {
        Assert.True(QuestionRules.HasNoOptions([]));
        Assert.True(QuestionRules.OptionsAreValid(QuestionType.Essay, []));
    }

    [Theory]
    [InlineData(new[] { false })]
    [InlineData(new[] { true })]
    [InlineData(new[] { true, false })]
    public void Essay_WithAnyOption_IsInvalid(bool[] correct)
    {
        var options = Options(correct);

        Assert.False(QuestionRules.HasNoOptions(options));
        Assert.False(QuestionRules.OptionsAreValid(QuestionType.Essay, options));
    }

    [Fact]
    public void AnUnknownQuestionType_HasNoValidOptions()
    {
        Assert.False(QuestionRules.OptionsAreValid((QuestionType)99, []));
        Assert.False(QuestionRules.OptionsAreValid((QuestionType)99, Options([true])));
    }

    private static QuestionOption[] Options(bool[] correct) =>
        [.. correct.Select((isCorrect, i) => new QuestionOption { Text = $"Option {i + 1}", IsCorrect = isCorrect, Position = i + 1 })];
}
