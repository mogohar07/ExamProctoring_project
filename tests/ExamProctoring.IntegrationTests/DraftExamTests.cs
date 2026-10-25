using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExamProctoring.Application.Common;
using ExamProctoring.Domain.Exams;
using ExamProctoring.Domain.Users;
using ExamProctoring.Infrastructure.Persistence;
using ExamProctoring.IntegrationTests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static ExamProctoring.IntegrationTests.TestSupport.AuthApi;

namespace ExamProctoring.IntegrationTests;

// Owner decision 2026-10-25: the reviewed Word import is saved as a Draft exam in one step (POST
// /api/exams/with-questions). Questions may have no marks (null) while the exam is a Draft; publishing requires valid
// marks for every question, and a Published exam never gets a question without marks.
[Collection(ApiCollection.Name)]
public sealed class DraftExamTests(ApiFactory factory)
{
    private const string Path = "/api/exams/with-questions";

    private readonly AuthApi _api = new(factory);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Saving the reviewed exam as a Draft

    [Fact]
    public async Task CreateWithQuestions_Returns201_AndStoresADraft_WithMissingMarksKeptAsNull()
    {
        var (instructorId, token) = await InstructorAsync();

        var response = await PostAsync(token, Path, DraftExam(Mcq(marks: 2m), Essay(marks: null)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = await JsonAsync(response);
        var root = json.RootElement;
        Assert.Equal(["exam", "questions"], root.EnumerateObject().Select(p => p.Name));
        var exam = root.GetProperty("exam");
        Assert.Equal("Draft", exam.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, exam.GetProperty("publishedAt").ValueKind);
        Assert.Equal("Imported exam", exam.GetProperty("title").GetString());

        var questions = root.GetProperty("questions").EnumerateArray().ToList();
        Assert.Equal([1, 2], questions.Select(q => q.GetProperty("position").GetInt32()));
        Assert.Equal("2.00", questions[0].GetProperty("marks").GetRawText());
        Assert.Equal(JsonValueKind.Null, questions[1].GetProperty("marks").ValueKind);
        Assert.Equal([true, false], questions[0].GetProperty("options").EnumerateArray().Select(o => o.GetProperty("isCorrect").GetBoolean()));

        var examId = exam.GetProperty("id").GetGuid();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Exams.SingleAsync(e => e.Id == examId, Ct);
        Assert.Equal(ExamStatus.Draft, stored.Status);
        Assert.Equal(instructorId, stored.InstructorId);
        Assert.Equal([2m, null], await db.Questions.Where(q => q.ExamId == examId).OrderBy(q => q.Position).Select(q => q.Marks).ToListAsync(Ct));
        Assert.Equal(2, await db.QuestionOptions.CountAsync(o => db.Questions.Any(q => q.Id == o.QuestionId && q.ExamId == examId), Ct));
    }

    [Fact]
    public async Task OneInvalidQuestion_Returns400_NamingIt_AndSavesNothing()
    {
        var (instructorId, token) = await InstructorAsync();
        var oneOption = new { type = "Mcq", text = "Pick", marks = 1m, options = new[] { new { text = "Only", isCorrect = true } } };

        var response = await PostAsync(token, Path, DraftExam(Essay(marks: 1m), oneOption));

        using var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
        Assert.Equal("An MCQ question needs at least 2 options.", FieldError(problem, "questions[1].options"));
        Assert.Equal(0, await ExamCountAsync(instructorId));
    }

    [Fact]
    public async Task ExamAndQuestionErrors_AreReportedTogether()
    {
        var (instructorId, token) = await InstructorAsync();
        var body = new { title = "  ", startsAt = InDays(3), durationMinutes = 0, questions = new object[] { new { type = "Essay", text = " " }, Essay(-1m) } };

        var response = await PostAsync(token, Path, body);

        using var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
        Assert.Equal("Title is required.", FieldError(problem, "title"));
        Assert.NotNull(FieldError(problem, "durationMinutes"));
        Assert.Equal("Text is required.", FieldError(problem, "questions[0].text"));
        Assert.Equal("Marks must be greater than 0.", FieldError(problem, "questions[1].marks"));
        Assert.Equal(0, await ExamCountAsync(instructorId));
    }

    [Theory]
    [InlineData("""[]""", "questions", "At least one question is required.")]
    [InlineData("""null""", "questions", "At least one question is required.")]
    [InlineData("""[null]""", "questions[0]", "Question is required.")]
    public async Task MissingQuestions_Return400(string questions, string field, string message)
    {
        var (_, token) = await InstructorAsync();
        var body = $$"""{"title":"T","startsAt":"{{InDays(3)}}","durationMinutes":60,"questions":{{questions}}}""";

        var response = await Client(token).PostAsync(Path, new StringContent(body, Encoding.UTF8, "application/json"), Ct);

        using var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
        Assert.Equal(message, FieldError(problem, field));
    }

    [Fact]
    public async Task MoreThan500Questions_Return400()
    {
        var (instructorId, token) = await InstructorAsync();

        var response = await PostAsync(token, Path, DraftExam(Enumerable.Range(0, 501).Select(_ => Essay(1m)).ToArray()));

        using var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
        Assert.Equal("At most 500 questions can be saved at once.", FieldError(problem, "questions"));
        Assert.Equal(0, await ExamCountAsync(instructorId));
    }

    [Fact]
    public async Task AStartTimeInThePast_Returns400_AndSavesNothing()
    {
        var (instructorId, token) = await InstructorAsync();
        var body = new { title = "T", startsAt = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O"), durationMinutes = 60, questions = new[] { Essay(1m) } };

        var response = await PostAsync(token, Path, body);

        using var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
        Assert.Equal("Start time must be in the future.", FieldError(problem, "startsAt"));
        Assert.Equal(0, await ExamCountAsync(instructorId));
    }

    [Fact]
    public async Task WithoutAToken_Returns401()
    {
        var response = await factory.CreateClient().PostAsJsonAsync(Path, DraftExam(Essay(1m)), Ct);

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, ErrorCodes.TokenInvalid);
    }

    [Theory]
    [InlineData(RoleNames.Student)]
    [InlineData(RoleNames.Admin)]
    public async Task OtherRoles_Get403(string role)
    {
        var user = await _api.CreateUserAsync(role);
        var token = (await _api.LoginAsync(user)).AccessToken;

        var response = await PostAsync(token, Path, DraftExam(Essay(1m)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- Publishing requires marks

    [Fact]
    public async Task Publish_IsRefused_WhileAQuestionHasNoMarks_AndAllowedOnceTheyAreSet()
    {
        var (_, token) = await InstructorAsync();
        using var created = await JsonAsync(await PostAsync(token, Path, DraftExam(Mcq(marks: 2m), Essay(marks: null))));
        var examId = created.RootElement.GetProperty("exam").GetProperty("id").GetGuid();
        var essayId = created.RootElement.GetProperty("questions")[1].GetProperty("id").GetGuid();

        var refused = await Client(token).PostAsync($"/api/exams/{examId}/publish", null, Ct);

        using (var problem = await AssertProblemAsync(refused, HttpStatusCode.Conflict, ErrorCodes.ExamNotPublishable))
            Assert.Equal("Question 2 has no marks. Add marks before publishing.", problem.RootElement.GetProperty("detail").GetString());

        var fixedEssay = await Client(token).PutAsJsonAsync($"/api/questions/{essayId}", Essay(marks: 3m), Ct);
        Assert.Equal(HttpStatusCode.OK, fixedEssay.StatusCode);

        var published = await Client(token).PostAsync($"/api/exams/{examId}/publish", null, Ct);
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        using var exam = await JsonAsync(published);
        Assert.Equal("Published", exam.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ADraftQuestion_CanBeCreatedAndUpdated_WithoutMarks()
    {
        var (_, token) = await InstructorAsync();
        var examId = await CreateExamAsync(token);

        var created = await PostAsync(token, $"/api/exams/{examId}/questions", Essay(marks: null));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var question = await JsonAsync(created);
        Assert.Equal(JsonValueKind.Null, question.RootElement.GetProperty("marks").ValueKind);

        var id = question.RootElement.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await Client(token).PutAsJsonAsync($"/api/questions/{id}", Essay(marks: 4m), Ct)).StatusCode);
        var cleared = await Client(token).PutAsJsonAsync($"/api/questions/{id}", Mcq(marks: null), Ct);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);

        using var list = await JsonAsync(await Client(token).GetAsync($"/api/exams/{examId}/questions", Ct));
        Assert.Equal(JsonValueKind.Null, list.RootElement.GetProperty("items")[0].GetProperty("marks").ValueKind);
    }

    [Fact]
    public async Task APublishedExam_NeverGetsAQuestionWithoutMarks()
    {
        var (_, token) = await InstructorAsync();
        var examId = await CreateExamAsync(token);
        using var question = await JsonAsync(await PostAsync(token, $"/api/exams/{examId}/questions", Essay(marks: 5m)));
        var questionId = question.RootElement.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await Client(token).PostAsync($"/api/exams/{examId}/publish", null, Ct)).StatusCode);

        var added = await PostAsync(token, $"/api/exams/{examId}/questions", Essay(marks: null));
        using (var problem = await AssertProblemAsync(added, HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed))
            Assert.Equal("Marks are required for a question of a published exam.", FieldError(problem, "marks"));

        var cleared = await Client(token).PutAsJsonAsync($"/api/questions/{questionId}", Essay(marks: null), Ct);
        using (var problem = await AssertProblemAsync(cleared, HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed))
            Assert.Equal("Marks are required for a question of a published exam.", FieldError(problem, "marks"));

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal([5m], await db.Questions.Where(q => q.ExamId == examId).Select(q => q.Marks).ToListAsync(Ct));
    }

    // ---- The whole workflow, from the Word file

    [Fact]
    public async Task AnImportedQuestionWithoutMarks_IsSavedAsNull_AndBlocksPublishing()
    {
        var (_, token) = await InstructorAsync();
        var docx = new DocxBuilder().Paragraphs(
            "1. Capital of France?", "*A) Paris", "B) Lyon", "Marks: 2",
            "2. Explain the water cycle.", "Model answer: Evaporation and rain.").Build();
        using var form = new MultipartFormDataContent { { new ByteArrayContent(docx), "file", "exam.docx" } };
        using var imported = await JsonAsync(await Client(token).PostAsync("/api/exams/import/docx", form, Ct));
        var importedQuestions = imported.RootElement.GetProperty("questions");
        Assert.Equal(JsonValueKind.Null, importedQuestions[1].GetProperty("marks").ValueKind);
        Assert.Equal("NeedsAttention", importedQuestions[1].GetProperty("status").GetString());

        // The app sends the reviewed questions as they are, with the exam details.
        var body = new JsonObject
        {
            ["title"] = "From Word",
            ["startsAt"] = InDays(3),
            ["durationMinutes"] = 45,
            ["questions"] = JsonNode.Parse(importedQuestions.GetRawText()),
        };
        var saved = await Client(token).PostAsync(Path, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        using var draft = await JsonAsync(saved);
        Assert.Equal("Draft", draft.RootElement.GetProperty("exam").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, draft.RootElement.GetProperty("questions")[1].GetProperty("marks").ValueKind);

        var examId = draft.RootElement.GetProperty("exam").GetProperty("id").GetGuid();
        using var problem = await AssertProblemAsync(
            await Client(token).PostAsync($"/api/exams/{examId}/publish", null, Ct), HttpStatusCode.Conflict, ErrorCodes.ExamNotPublishable);
        Assert.Equal("Question 2 has no marks. Add marks before publishing.", problem.RootElement.GetProperty("detail").GetString());
    }

    // ---- Helpers

    private static string InDays(int days) => DateTimeOffset.UtcNow.AddDays(days).ToString("O");

    private static object DraftExam(params object[] questions) =>
        new { title = "Imported exam", description = (string?)null, startsAt = InDays(3), durationMinutes = 60, questions };

    private static object Mcq(decimal? marks) => new
    {
        type = "Mcq",
        text = "Capital of France?",
        marks,
        options = new[] { new { text = "Paris", isCorrect = true }, new { text = "Lyon", isCorrect = false } },
    };

    private static object Essay(decimal? marks) => new { type = "Essay", text = "Explain the water cycle.", marks, modelAnswer = "Evaporation." };

    private async Task<(Guid Id, string Token)> InstructorAsync()
    {
        var user = await _api.CreateUserAsync(RoleNames.Instructor);
        return (user.Id, (await _api.LoginAsync(user)).AccessToken);
    }

    private HttpClient Client(string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private Task<HttpResponseMessage> PostAsync(string token, string path, object body) =>
        Client(token).PostAsJsonAsync(path, body, Ct);

    private async Task<Guid> CreateExamAsync(string token)
    {
        var response = await PostAsync(token, "/api/exams", new { title = "Exam", startsAt = InDays(3), durationMinutes = 60 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = await JsonAsync(response);
        return json.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<int> ExamCountAsync(Guid instructorId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Exams.CountAsync(e => e.InstructorId == instructorId, Ct);
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

    private static string? FieldError(JsonDocument problem, string field) =>
        problem.RootElement.GetProperty("errors").TryGetProperty(field, out var messages) ? messages[0].GetString() : null;
}
