using ExamProctoring.Domain.Exams;

namespace ExamProctoring.UnitTests.Exams;

// An MCQ has exactly one correct option (§2.2, §5.3); an Essay has no options (§5.2, confirmed 2026-10-21).
public sealed class QuestionRulesTests
{
    [Theory]
    [InlineData(new[] { true })]
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
