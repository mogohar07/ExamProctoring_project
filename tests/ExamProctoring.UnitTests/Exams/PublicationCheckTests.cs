using ExamProctoring.Application.Exams;
using ExamProctoring.Domain.Exams;

namespace ExamProctoring.UnitTests.Exams;

// D34, D36: publication checks every question against D31-D32 and the current MCQ maximum, and names the first problem.
public sealed class PublicationCheckTests
{
    [Fact]
    public void AnExamWithValidQuestions_HasNoProblem()
    {
        var mcq = Mcq(1, ("Paris", true), ("Lyon", false));
        var essay = Essay(2);

        Assert.Null(PublicationCheck.FirstProblem([mcq.Question, essay], mcq.Options, maxMcqOptions: 8));
    }

    [Fact]
    public void AnExamWithoutQuestions_CannotBePublished() =>
        Assert.Equal("The exam has no questions.", PublicationCheck.FirstProblem([], [], maxMcqOptions: 8));

    [Fact]
    public void AQuestionAboveTheCurrentMaximum_BlocksPublication()
    {
        var mcq = Mcq(1, ("A", true), ("B", false), ("C", false), ("D", false));

        Assert.Null(PublicationCheck.FirstProblem([mcq.Question], mcq.Options, maxMcqOptions: 4));
        Assert.Equal("Question 1 has more options than the current maximum of 3.",
            PublicationCheck.FirstProblem([mcq.Question], mcq.Options, maxMcqOptions: 3));
    }

    [Fact]
    public void TheFirstProblem_IsTheOneOfTheLowestPosition()
    {
        var third = Mcq(3, ("A", true), ("B", true));
        var second = Mcq(2, ("A", false), ("B", false));

        Assert.Equal("Question 2 needs exactly one correct option.",
            PublicationCheck.FirstProblem([third.Question, second.Question], [.. third.Options, .. second.Options], maxMcqOptions: 8));
    }

    [Fact]
    public void EachRule_HasItsOwnMessage()
    {
        Assert.Equal("Question 1 needs at least 2 options.", Problem(Mcq(1, ("Only", true))));
        Assert.Equal("Question 1 needs exactly one correct option.", Problem(Mcq(1, ("A", true), ("B", true))));
        Assert.Equal("Question 1 repeats an option text.", Problem(Mcq(1, ("Paris", true), (" paris", false))));
        Assert.Equal("Question 1 has an option without text.", Problem(Mcq(1, ("A", true), ("  ", false))));
        Assert.Equal("Question 1 has an option longer than 1000 characters.", Problem(Mcq(1, ("A", true), (new string('o', 1001), false))));

        var essayWithOption = Essay(1);
        Assert.Equal("Question 1 is an essay question and must not have options.",
            PublicationCheck.FirstProblem([essayWithOption], [Option(essayWithOption, "A", true, 1)], 8));

        Assert.Equal("Question 1 has no text.", Problem(Essay(1, q => q.Text = "  ")));
        Assert.Equal("Question 1 has more than 4000 characters of text.", Problem(Essay(1, q => q.Text = new string('q', 4001))));
        Assert.Equal("Question 1 has no marks. Add marks before publishing.", Problem(Essay(1, q => q.Marks = null)));
        Assert.Equal("Question 1 has invalid marks.", Problem(Essay(1, q => q.Marks = 0m)));
        Assert.Equal("Question 1 has invalid marks.", Problem(Essay(1, q => q.Marks = -1m)));
        Assert.Equal("Question 1 has invalid marks.", Problem(Essay(1, q => q.Marks = 1.005m)));
        Assert.Equal("Question 1 has a model answer longer than 4000 characters.", Problem(Essay(1, q => q.ModelAnswer = new string('a', 4001))));
    }

    private static string? Problem((Question Question, List<QuestionOption> Options) mcq) =>
        PublicationCheck.FirstProblem([mcq.Question], mcq.Options, maxMcqOptions: 8);

    private static string? Problem(Question essay) =>
        PublicationCheck.FirstProblem([essay], [], maxMcqOptions: 8);

    private static (Question Question, List<QuestionOption> Options) Mcq(int position, params (string Text, bool IsCorrect)[] options)
    {
        var question = new Question { Id = Guid.NewGuid(), Type = QuestionType.Mcq, Text = $"Question {position}", Marks = 1m, Position = position };
        return (question, [.. options.Select((o, i) => Option(question, o.Text, o.IsCorrect, i + 1))]);
    }

    private static Question Essay(int position, Action<Question>? change = null)
    {
        var question = new Question { Id = Guid.NewGuid(), Type = QuestionType.Essay, Text = $"Question {position}", Marks = 5m, Position = position };
        change?.Invoke(question);
        return question;
    }

    private static QuestionOption Option(Question question, string text, bool isCorrect, int position) =>
        new() { Id = Guid.NewGuid(), QuestionId = question.Id, Text = text, IsCorrect = isCorrect, Position = position };
}
