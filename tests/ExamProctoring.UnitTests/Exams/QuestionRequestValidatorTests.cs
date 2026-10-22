using ExamProctoring.Application.Abstractions;
using ExamProctoring.Application.Common;
using ExamProctoring.Application.Exams;

namespace ExamProctoring.UnitTests.Exams;

// Question create and replace fields (D31, D32, D35): type Mcq or Essay; text 1-4000 after trimming; marks greater than
// 0, at most 9999.99, two decimals at most; model answer optional, at most 4000; an MCQ has 2 to Exams:MaxMcqOptions
// options with exactly one correct and no repeated text; an Essay has none.
public sealed class QuestionRequestValidatorTests
{
    private readonly QuestionRequestValidator _validator = new(new Settings(maxMcqOptions: 8));

    [Fact]
    public void ValidMcq_IsValid() =>
        Assert.True(_validator.Validate(Mcq()).IsValid);

    [Fact]
    public void ValidEssay_WithMissingOrEmptyOptions_IsValid()
    {
        Assert.True(_validator.Validate(Essay(options: null)).IsValid);
        Assert.True(_validator.Validate(Essay(options: [])).IsValid);
    }

    [Theory]
    [InlineData("Mcq")]
    [InlineData("mcq")]
    [InlineData("MCQ")]
    public void Type_Mcq_InAnyCase_IsValid(string type) =>
        Assert.True(_validator.Validate(Mcq() with { Type = type }).IsValid);

    [Theory]
    [InlineData("Essay")]
    [InlineData("essay")]
    public void Type_Essay_InAnyCase_IsValid(string type) =>
        Assert.True(_validator.Validate(Essay() with { Type = type }).IsValid);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Type_Missing_IsRejected(string? type) =>
        AssertOnlyFieldFails(Essay() with { Type = type }, "Type", "Type is required.");

    // No option rules apply while the type is unknown.
    [Theory]
    [InlineData("TrueFalse")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData(" Mcq")]
    public void Type_AnythingElse_IsRejected(string type) =>
        AssertOnlyFieldFails(Mcq() with { Type = type, Options = null }, "Type", "Type must be Mcq or Essay.");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public void Text_Missing_IsRejected(string? text) =>
        AssertOnlyFieldFails(Mcq() with { Text = text }, "Text", "Text is required.");

    [Fact]
    public void Text_Of4000Characters_AfterTrimming_IsValid() =>
        Assert.True(_validator.Validate(Mcq() with { Text = "  " + new string('q', 4000) + "  " }).IsValid);

    [Fact]
    public void Text_Over4000Characters_IsRejected() =>
        AssertOnlyFieldFails(Mcq() with { Text = new string('q', 4001) }, "Text", "Text must be at most 4000 characters.");

    [Theory]
    [InlineData("0.01")]
    [InlineData("1")]
    [InlineData("2.5")]
    [InlineData("1.500")]
    [InlineData("9999.99")]
    public void Marks_WithinTheRules_AreValid(string marks) =>
        Assert.True(_validator.Validate(Mcq() with { Marks = decimal.Parse(marks, System.Globalization.CultureInfo.InvariantCulture) }).IsValid);

    [Fact]
    public void Marks_Missing_IsRejected() =>
        AssertOnlyFieldFails(Mcq() with { Marks = null }, "Marks", "Marks are required.");

    [Theory]
    [InlineData("0", "Marks must be greater than 0.")]
    [InlineData("-1", "Marks must be greater than 0.")]
    [InlineData("-0.001", "Marks must be greater than 0.")]
    [InlineData("10000", "Marks must be at most 9999.99.")]
    [InlineData("9999.991", "Marks must be at most 9999.99.")]
    [InlineData("0.001", "Marks can have at most 2 decimal places.")]
    [InlineData("1.005", "Marks can have at most 2 decimal places.")]
    public void Marks_OutsideTheRules_AreRejected_WithOneMessage(string marks, string message) =>
        AssertOnlyFieldFails(Mcq() with { Marks = decimal.Parse(marks, System.Globalization.CultureInfo.InvariantCulture) }, "Marks", message);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ModelAnswer_IsOptional(string? modelAnswer) =>
        Assert.True(_validator.Validate(Essay() with { ModelAnswer = modelAnswer }).IsValid);

    [Fact]
    public void ModelAnswer_Of4000Characters_AfterTrimming_IsValid() =>
        Assert.True(_validator.Validate(Essay() with { ModelAnswer = " " + new string('a', 4000) + " " }).IsValid);

    [Fact]
    public void ModelAnswer_Over4000Characters_IsRejected() =>
        AssertOnlyFieldFails(Essay() with { ModelAnswer = new string('a', 4001) }, "ModelAnswer", "Model answer must be at most 4000 characters.");

    // ---- MCQ options ----

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(8)]
    public void Mcq_From2To8Options_IsValid(int count) =>
        Assert.True(_validator.Validate(Mcq() with { Options = Options(count) }).IsValid);

    [Fact]
    public void Mcq_WithOneOption_IsRejected() =>
        AssertOnlyFieldFails(Mcq() with { Options = Options(1) }, "Options", "An MCQ question needs at least 2 options.");

    [Fact]
    public void Mcq_WithoutOptions_NeedsTwoOptions_AndOneCorrect()
    {
        var errors = _validator.Validate(Mcq() with { Options = null }).Errors;

        Assert.All(errors, e => Assert.Equal("Options", e.PropertyName));
        Assert.Equal(
            new[] { "An MCQ question needs at least 2 options.", "An MCQ question needs exactly one correct option." },
            errors.Select(e => e.ErrorMessage));
    }

    [Fact]
    public void Mcq_OverTheConfiguredMaximum_IsRejected() =>
        AssertOnlyFieldFails(Mcq() with { Options = Options(9) }, "Options", "An MCQ question can have at most 8 options.");

    [Fact]
    public void Mcq_Maximum_ComesFromTheSettings()
    {
        var validator = new QuestionRequestValidator(new Settings(maxMcqOptions: 3));

        Assert.True(validator.Validate(Mcq() with { Options = Options(3) }).IsValid);
        var error = Assert.Single(validator.Validate(Mcq() with { Options = Options(4) }).Errors);
        Assert.Equal("Options", error.PropertyName);
        Assert.Equal("An MCQ question can have at most 3 options.", error.ErrorMessage);
    }

    [Theory]
    [InlineData(new[] { false, false })]
    [InlineData(new[] { true, true })]
    [InlineData(new[] { true, false, true })]
    public void Mcq_WithoutExactlyOneCorrectOption_IsRejected(bool[] correct) =>
        AssertOnlyFieldFails(
            Mcq() with { Options = [.. correct.Select((isCorrect, i) => new QuestionOptionRequest($"Option {i + 1}", isCorrect))] },
            "Options", "An MCQ question needs exactly one correct option.");

    [Fact]
    public void Mcq_OptionWithoutIsCorrect_CountsAsWrong() =>
        Assert.True(_validator.Validate(Mcq() with { Options = [new("Paris", true), new("Lyon", null), new("Nice", null)] }).IsValid);

    [Theory]
    [InlineData("Paris", "paris")]
    [InlineData("Paris", "  PARIS ")]
    public void Mcq_WithRepeatedOptionText_IsRejected(string first, string second) =>
        AssertOnlyFieldFails(Mcq() with { Options = [new(first, true), new(second, false)] }, "Options", "Options must not repeat the same text.");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Mcq_OptionWithoutText_IsRejected_OnThatOption(string? text) =>
        AssertOnlyFieldFails(Mcq() with { Options = [new("Paris", true), new(text, false), new("Nice", false)] },
            "Options[1].Text", "Option text is required.");

    [Fact]
    public void Mcq_OptionText_Of1000Characters_AfterTrimming_IsValid() =>
        Assert.True(_validator.Validate(Mcq() with { Options = [new(" " + new string('o', 1000) + " ", true), new("Other", false)] }).IsValid);

    [Fact]
    public void Mcq_OptionText_Over1000Characters_IsRejected() =>
        AssertOnlyFieldFails(Mcq() with { Options = [new("Paris", true), new(new string('o', 1001), false)] },
            "Options[1].Text", "Option text must be at most 1000 characters.");

    [Fact]
    public void Mcq_MissingOption_IsRejected_OnThatOption() =>
        AssertOnlyFieldFails(Mcq() with { Options = [new("Paris", true), new("Lyon", false), null] }, "Options[2]", "Option is required.");

    // ---- Essay ----

    [Fact]
    public void Essay_WithOptions_IsRejected() =>
        AssertOnlyFieldFails(Essay(options: [new("Paris", true), new("Lyon", false)]), "Options", "An Essay question has no options.");

    [Fact]
    public void Essay_OptionsAreNotCheckedOneByOne() =>
        AssertOnlyFieldFails(Essay(options: [new(null, null)]), "Options", "An Essay question has no options.");

    // ---- Settings ----

    [Fact]
    public void MaxMcqOptions_DefaultsTo8_AndMayNotGoBelow2()
    {
        Assert.Equal(8, new ExamOptions().MaxMcqOptions);
        Assert.Equal(2, ExamOptions.MinMaxMcqOptions);
    }

    private static QuestionRequest Mcq() =>
        new("Mcq", "Capital of France?", 2m, "Paris is the capital.", [new("Paris", true), new("Lyon", false), new("Nice", false)]);

    private static QuestionRequest Essay(IReadOnlyList<QuestionOptionRequest?>? options = null) =>
        new("Essay", "Explain photosynthesis.", 10m, null, options);

    private static QuestionOptionRequest[] Options(int count) =>
        [.. Enumerable.Range(1, count).Select(i => new QuestionOptionRequest($"Option {i}", i == 1))];

    private void AssertOnlyFieldFails(QuestionRequest request, string field, string message)
    {
        var error = Assert.Single(_validator.Validate(request).Errors);
        Assert.Equal(field, error.PropertyName);
        Assert.Equal(message, error.ErrorMessage);
    }

    private sealed class Settings(int maxMcqOptions) : IExamSettings
    {
        public int WarningLimit => 20;

        public int MaxMcqOptions => maxMcqOptions;
    }
}
