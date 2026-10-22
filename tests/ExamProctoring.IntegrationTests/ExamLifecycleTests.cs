using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ExamProctoring.Application.Common;
using ExamProctoring.Domain.Exams;
using ExamProctoring.Domain.Users;
using ExamProctoring.Infrastructure.Persistence;
using ExamProctoring.IntegrationTests.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using static ExamProctoring.IntegrationTests.TestSupport.AuthApi;

namespace ExamProctoring.IntegrationTests;

// Phase 2 Step 2.4: publish, unpublish and cancel (D30, D34, D36). Each test uses its own new instructor and exams.
[Collection(ApiCollection.Name)]
public sealed class ExamLifecycleTests(ApiFactory factory)
{
    private static readonly string[] ExamFields =
    [
        "id", "title", "description", "startsAt", "durationMinutes", "endsAt", "status", "phase", "warningLimit", "publishedAt",
    ];

    private static readonly string[] Actions = ["publish", "unpublish", "cancel"];

    private readonly AuthApi _api = new(factory);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Publish ----

    [Fact]
    public async Task Publish_AValidDraft_Returns200_WithTheExam_AndRecordsTheTime()
    {
        var (instructor, token) = await InstructorAsync();
        var startsAt = InADay();
        var exam = await InsertExamAsync(instructor.Id, e => e.StartsAt = startsAt);
        await InsertQuestionAsync(exam.Id, 1);
        await InsertQuestionAsync(exam.Id, 2, essay: true);
        var now = startsAt.AddHours(-5);

        HttpResponseMessage response;
        using (factory.Clock.FreezeAt(now))
            response = await ActAsync("publish", exam.Id.ToString(), token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonAsync(response);
        Assert.Equal(ExamFields, json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(exam.Id, json.RootElement.GetProperty("id").GetGuid());
        Assert.Equal("Published", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("Upcoming", json.RootElement.GetProperty("phase").GetString());
        Assert.Equal(now, json.RootElement.GetProperty("publishedAt").GetDateTimeOffset());

        var stored = (await StoredExamAsync(exam.Id))!;
        Assert.Equal(ExamStatus.Published, stored.Status);
        Assert.Equal(now, stored.PublishedAt);
        Assert.Equal(exam.Title, stored.Title);
        Assert.Equal(exam.StartsAt, stored.StartsAt);
    }

    [Fact]
    public async Task Publish_JustBeforeTheStart_Succeeds()
    {
        var (instructor, token) = await InstructorAsync();
        var startsAt = InADay();
        var exam = await InsertExamAsync(instructor.Id, e => e.StartsAt = startsAt);
        await InsertQuestionAsync(exam.Id, 1);

        using (factory.Clock.FreezeAt(startsAt.AddTicks(-1)))
            Assert.Equal(HttpStatusCode.OK, (await ActAsync("publish", exam.Id.ToString(), token)).StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(600)]
    public async Task Publish_FromTheStartTimeOn_Returns409(int minutesAfterStart)
    {
        var (instructor, token) = await InstructorAsync();
        var startsAt = InADay();
        var exam = await InsertExamAsync(instructor.Id, e => e.StartsAt = startsAt);
        await InsertQuestionAsync(exam.Id, 1);

        using (factory.Clock.FreezeAt(startsAt.AddMinutes(minutesAfterStart)))
            await AssertRefusedAsync(await ActAsync("publish", exam.Id.ToString(), token), ErrorCodes.ExamNotPublishable, "The start time has passed.");

        await AssertUnchangedAsync(exam);
    }

    [Fact]
    public async Task Publish_WithoutQuestions_Returns409()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id);

        await AssertRefusedAsync(await ActAsync("publish", exam.Id.ToString(), token), ErrorCodes.ExamNotPublishable, "The exam has no questions.");

        await AssertUnchangedAsync(exam);
    }

    // Stored questions written outside the API, so each rule is checked again at publication (D34).
    [Theory]
    [InlineData("two correct", "Question 2 needs exactly one correct option.")]
    [InlineData("one option", "Question 2 needs at least 2 options.")]
    [InlineData("essay with options", "Question 2 is an essay question and must not have options.")]
    [InlineData("repeated text", "Question 2 repeats an option text.")]
    public async Task Publish_WithAnInvalidQuestion_Returns409_NamingTheFirstProblem(string problem, string message)
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id);
        await InsertQuestionAsync(exam.Id, 1);
        var broken = NewQuestion(exam.Id, 2, problem == "essay with options" ? QuestionType.Essay : QuestionType.Mcq);
        var options = problem switch
        {
            "two correct" => new[] { ("A", true), ("B", true) },
            "one option" => [("A", true)],
            "essay with options" => [("A", true), ("B", false)],
            _ => [("Same", true), ("SAME", false)],
        };
        await InsertAsync(broken, options);
        await InsertQuestionAsync(exam.Id, 3, correctOptions: 0);

        await AssertRefusedAsync(await ActAsync("publish", exam.Id.ToString(), token), ErrorCodes.ExamNotPublishable, message);

        await AssertUnchangedAsync(exam);
    }

    // S7, moved from Step 2.3: a question written under a higher maximum blocks publication until an edit fixes it.
    [Fact]
    public async Task Publish_WithAQuestionAboveALoweredMaximum_Returns409_UntilTheQuestionIsCorrected()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id);
        await InsertQuestionAsync(exam.Id, 1);
        var overLimit = await InsertQuestionAsync(exam.Id, 2, optionCount: 5);

        await using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("Exams:MaxMcqOptions", "3"));
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await AssertRefusedAsync(await client.PostAsync($"/api/exams/{exam.Id}/publish", null, Ct),
            ErrorCodes.ExamNotPublishable, "Question 2 has more options than the current maximum of 3.");
        await AssertUnchangedAsync(exam);

        var fix = await client.PutAsJsonAsync($"/api/questions/{overLimit.Id}", new
        {
            type = "Mcq", text = "Fixed", marks = 1, options = new[] { new { text = "A", isCorrect = true }, new { text = "B", isCorrect = false } },
        }, Ct);
        Assert.Equal(HttpStatusCode.OK, fix.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/exams/{exam.Id}/publish", null, Ct)).StatusCode);
        Assert.Equal(ExamStatus.Published, (await StoredExamAsync(exam.Id))!.Status);
    }

    [Fact]
    public async Task Publish_UnderTheDefaultMaximum_AcceptsEightOptions()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id);
        await InsertQuestionAsync(exam.Id, 1, optionCount: 8);

        Assert.Equal(HttpStatusCode.OK, (await ActAsync("publish", exam.Id.ToString(), token)).StatusCode);
    }

    [Theory]
    [InlineData(ExamStatus.Published, "The exam is already published.")]
    [InlineData(ExamStatus.Cancelled, "A cancelled exam cannot be published.")]
    public async Task Publish_AnExamThatIsNotADraft_Returns409(ExamStatus status, string message)
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id, e => SetStatus(e, status));
        await InsertQuestionAsync(exam.Id, 1);

        await AssertRefusedAsync(await ActAsync("publish", exam.Id.ToString(), token), ErrorCodes.ExamNotPublishable, message);

        await AssertUnchangedAsync(exam);
    }

    // ---- Unpublish ----

    [Fact]
    public async Task Unpublish_APublishedExam_ReturnsItToDraft_AndClearsPublishedAt_ThenItCanBePublishedAgain()
    {
        var (instructor, token) = await InstructorAsync();
        var startsAt = InADay();
        var exam = await InsertExamAsync(instructor.Id, e => e.StartsAt = startsAt);
        await InsertQuestionAsync(exam.Id, 1);

        using (factory.Clock.FreezeAt(startsAt.AddHours(-10)))
            Assert.Equal(HttpStatusCode.OK, (await ActAsync("publish", exam.Id.ToString(), token)).StatusCode);

        HttpResponseMessage response;
        using (factory.Clock.FreezeAt(startsAt.AddHours(-9)))
            response = await ActAsync("unpublish", exam.Id.ToString(), token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var json = await JsonAsync(response))
        {
            Assert.Equal(ExamFields, json.RootElement.EnumerateObject().Select(p => p.Name));
            Assert.Equal("Draft", json.RootElement.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("publishedAt").ValueKind);
        }

        var stored = (await StoredExamAsync(exam.Id))!;
        Assert.Equal(ExamStatus.Draft, stored.Status);
        Assert.Null(stored.PublishedAt);

        // Publishing again records the new time (D34).
        using (factory.Clock.FreezeAt(startsAt.AddHours(-8)))
            Assert.Equal(HttpStatusCode.OK, (await ActAsync("publish", exam.Id.ToString(), token)).StatusCode);
        Assert.Equal(startsAt.AddHours(-8), (await StoredExamAsync(exam.Id))!.PublishedAt);
    }

    [Theory]
    [InlineData(ExamStatus.Draft, "The exam is not published.")]
    [InlineData(ExamStatus.Cancelled, "A cancelled exam cannot be unpublished.")]
    public async Task Unpublish_AnExamThatIsNotPublished_Returns409(ExamStatus status, string message)
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id, e => SetStatus(e, status));

        await AssertRefusedAsync(await ActAsync("unpublish", exam.Id.ToString(), token), ErrorCodes.ExamNotUnpublishable, message);

        await AssertUnchangedAsync(exam);
    }

    // ---- Cancel ----

    [Fact]
    public async Task Cancel_APublishedExam_Returns200_IsFinal_AndKeepsTheExamVisible()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id, e => SetStatus(e, ExamStatus.Published));
        var question = await InsertQuestionAsync(exam.Id, 1);

        var response = await ActAsync("cancel", exam.Id.ToString(), token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var json = await JsonAsync(response))
        {
            Assert.Equal(ExamFields, json.RootElement.EnumerateObject().Select(p => p.Name));
            Assert.Equal("Cancelled", json.RootElement.GetProperty("status").GetString());
            Assert.Equal(exam.PublishedAt, json.RootElement.GetProperty("publishedAt").GetDateTimeOffset());
        }

        var cancelled = (await StoredExamAsync(exam.Id))!;
        Assert.Equal(ExamStatus.Cancelled, cancelled.Status);
        Assert.Equal(exam.PublishedAt, cancelled.PublishedAt);

        // Cancelled is terminal (D30): nothing else is allowed, and the exam and its questions stay.
        await AssertRefusedAsync(await ActAsync("publish", exam.Id.ToString(), token), ErrorCodes.ExamNotPublishable, "A cancelled exam cannot be published.");
        await AssertRefusedAsync(await ActAsync("unpublish", exam.Id.ToString(), token), ErrorCodes.ExamNotUnpublishable, "A cancelled exam cannot be unpublished.");
        await AssertRefusedAsync(await ActAsync("cancel", exam.Id.ToString(), token), ErrorCodes.ExamNotCancellable, "The exam is already cancelled.");
        using (await AssertProblemAsync(await SendAsync(HttpMethod.Delete, $"/api/exams/{exam.Id}", token), HttpStatusCode.Conflict, ErrorCodes.ExamNotEditable)) { }
        using (await AssertProblemAsync(await SendAsync(HttpMethod.Put, $"/api/exams/{exam.Id}", token,
                   JsonContent.Create(new { title = "Again", startsAt = exam.StartsAt.ToString("O"), durationMinutes = 60 })),
                   HttpStatusCode.Conflict, ErrorCodes.ExamNotEditable)) { }
        using (await AssertProblemAsync(await SendAsync(HttpMethod.Delete, $"/api/questions/{question.Id}", token), HttpStatusCode.Conflict, ErrorCodes.ExamNotEditable)) { }

        Assert.Equal(cancelled.RowVersion, (await StoredExamAsync(exam.Id))!.RowVersion);
        using var exams = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/exams?status=cancelled", token));
        Assert.Equal(exam.Id, Assert.Single(exams.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Cancel_ADraft_Returns409()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id);

        await AssertRefusedAsync(await ActAsync("cancel", exam.Id.ToString(), token), ErrorCodes.ExamNotCancellable, "Only a published exam can be cancelled.");

        await AssertUnchangedAsync(exam);
    }

    // ---- Start time, for unpublish and cancel ----

    [Theory]
    [InlineData("unpublish", 0)]
    [InlineData("unpublish", 30)]
    [InlineData("unpublish", 600)]
    [InlineData("cancel", 0)]
    [InlineData("cancel", 30)]
    [InlineData("cancel", 600)]
    public async Task UnpublishOrCancel_FromTheStartTimeOn_Returns409(string action, int minutesAfterStart)
    {
        var (instructor, token) = await InstructorAsync();
        var startsAt = InADay();
        var exam = await InsertExamAsync(instructor.Id, e =>
        {
            e.StartsAt = startsAt;
            SetStatus(e, ExamStatus.Published);
        });

        using (factory.Clock.FreezeAt(startsAt.AddMinutes(minutesAfterStart)))
            await AssertRefusedAsync(await ActAsync(action, exam.Id.ToString(), token),
                action == "cancel" ? ErrorCodes.ExamNotCancellable : ErrorCodes.ExamNotUnpublishable, "The start time has passed.");

        await AssertUnchangedAsync(exam);
    }

    [Theory]
    [InlineData("unpublish")]
    [InlineData("cancel")]
    public async Task UnpublishOrCancel_JustBeforeTheStart_Succeeds(string action)
    {
        var (instructor, token) = await InstructorAsync();
        var startsAt = InADay();
        var exam = await InsertExamAsync(instructor.Id, e =>
        {
            e.StartsAt = startsAt;
            SetStatus(e, ExamStatus.Published);
        });

        using (factory.Clock.FreezeAt(startsAt.AddTicks(-1)))
            Assert.Equal(HttpStatusCode.OK, (await ActAsync(action, exam.Id.ToString(), token)).StatusCode);
    }

    // ---- Ownership and authorization ----

    public static TheoryData<string> AllActions() => new(Actions);

    public static TheoryData<string, string> ActionsByOtherRoles()
    {
        var data = new TheoryData<string, string>();
        foreach (var action in Actions)
        {
            data.Add(action, RoleNames.Student);
            data.Add(action, RoleNames.Admin);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllActions))]
    public async Task AnotherInstructorsExam_AnUnknownId_OrNotAnId_Returns404(string action)
    {
        var (_, token) = await InstructorAsync();
        var otherInstructor = await _api.CreateUserAsync(RoleNames.Instructor);
        var othersExam = await InsertExamAsync(otherInstructor.Id, e => SetStatus(e, action == "publish" ? ExamStatus.Draft : ExamStatus.Published));
        await InsertQuestionAsync(othersExam.Id, 1);

        foreach (var id in new[] { othersExam.Id.ToString(), Guid.NewGuid().ToString(), "not-an-id" })
            using (await AssertProblemAsync(await ActAsync(action, id, token), HttpStatusCode.NotFound, ErrorCodes.ExamNotFound)) { }

        await AssertUnchangedAsync(othersExam);
    }

    [Theory]
    [MemberData(nameof(ActionsByOtherRoles))]
    public async Task StudentsAndAdmins_Get403_AndChangeNothing(string action, string role)
    {
        var (instructor, _) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id, e => SetStatus(e, action == "publish" ? ExamStatus.Draft : ExamStatus.Published));
        await InsertQuestionAsync(exam.Id, 1);
        var caller = await _api.CreateUserAsync(role);
        var session = await _api.LoginAsync(caller);

        Assert.Equal(HttpStatusCode.Forbidden, (await ActAsync(action, exam.Id.ToString(), session.AccessToken)).StatusCode);

        await AssertUnchangedAsync(exam);
    }

    [Theory]
    [MemberData(nameof(AllActions))]
    public async Task WithoutAToken_Returns401TokenInvalid(string action)
    {
        var (instructor, _) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id, e => SetStatus(e, action == "publish" ? ExamStatus.Draft : ExamStatus.Published));
        await InsertQuestionAsync(exam.Id, 1);

        using (await AssertProblemAsync(await ActAsync(action, exam.Id.ToString(), accessToken: null), HttpStatusCode.Unauthorized, ErrorCodes.TokenInvalid)) { }

        await AssertUnchangedAsync(exam);
    }

    [Theory]
    [MemberData(nameof(AllActions))]
    public async Task AnInstructorWhoMustChangeTheirPassword_Gets403PasswordChangeRequired(string action)
    {
        var instructor = await _api.CreateUserAsync(RoleNames.Instructor, mustChangePassword: true,
            temporaryPasswordExpiresAt: DateTimeOffset.UtcNow.AddHours(72));
        var session = await _api.LoginAsync(instructor);
        var exam = await InsertExamAsync(instructor.Id, e => SetStatus(e, action == "publish" ? ExamStatus.Draft : ExamStatus.Published));
        await InsertQuestionAsync(exam.Id, 1);

        using (await AssertProblemAsync(await ActAsync(action, exam.Id.ToString(), session.AccessToken),
                   HttpStatusCode.Forbidden, ErrorCodes.PasswordChangeRequired)) { }

        await AssertUnchangedAsync(exam);
    }

    // ---- Writes to the same exam at the same moment (Phase 2 plan, R2) ----

    [Fact]
    public async Task PublishDuringADeleteOfTheLastQuestion_Returns409_NoQuestions()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id);
        var question = await InsertQuestionAsync(exam.Id, 1);

        // A question delete has locked the exam and removed its only question, but has not committed yet.
        await using var deletion = await HeldTransaction.BeginAsync(factory);
        await deletion.Db.Exams.Where(e => e.Id == exam.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.Title, e => e.Title), Ct);
        await deletion.Db.Questions.Where(q => q.Id == question.Id).ExecuteDeleteAsync(Ct);

        var publish = ActAsync("publish", exam.Id.ToString(), token);
        await deletion.WaitUntilItBlocksAnotherRequestAsync(factory);
        await deletion.CommitAsync();

        await AssertRefusedAsync(await publish, ErrorCodes.ExamNotPublishable, "The exam has no questions.");
        Assert.Equal(ExamStatus.Draft, (await StoredExamAsync(exam.Id))!.Status);
    }

    [Fact]
    public async Task PublishDuringAnotherPublish_Returns409_AndKeepsTheFirstTime()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id);
        await InsertQuestionAsync(exam.Id, 1);
        var firstTime = DateTimeOffset.UtcNow.AddMinutes(-1);

        await using var otherPublish = await HeldTransaction.BeginAsync(factory);
        await otherPublish.Db.Exams.Where(e => e.Id == exam.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, ExamStatus.Published).SetProperty(e => e.PublishedAt, firstTime), Ct);

        var publish = ActAsync("publish", exam.Id.ToString(), token);
        await otherPublish.WaitUntilItBlocksAnotherRequestAsync(factory);
        await otherPublish.CommitAsync();

        await AssertRefusedAsync(await publish, ErrorCodes.ExamNotPublishable, "The exam is already published.");
        Assert.Equal(firstTime, (await StoredExamAsync(exam.Id))!.PublishedAt);
    }

    [Fact]
    public async Task CancelDuringAnUnpublish_Returns409_AndTheExamStaysADraft()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id, e => SetStatus(e, ExamStatus.Published));

        await using var unpublish = await HeldTransaction.BeginAsync(factory);
        await unpublish.Db.Exams.Where(e => e.Id == exam.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, ExamStatus.Draft).SetProperty(e => e.PublishedAt, (DateTimeOffset?)null), Ct);

        var cancel = ActAsync("cancel", exam.Id.ToString(), token);
        await unpublish.WaitUntilItBlocksAnotherRequestAsync(factory);
        await unpublish.CommitAsync();

        await AssertRefusedAsync(await cancel, ErrorCodes.ExamNotCancellable, "Only a published exam can be cancelled.");
        Assert.Equal(ExamStatus.Draft, (await StoredExamAsync(exam.Id))!.Status);
    }

    [Fact]
    public async Task QuestionEditDuringACancellation_Returns409()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id, e => SetStatus(e, ExamStatus.Published));
        var question = await InsertQuestionAsync(exam.Id, 1);

        // The real cancel endpoint holds the lock here: the test holds a lock on the exam row first, the cancel waits,
        // and a question edit started after it waits behind both.
        await using var gate = await HeldTransaction.BeginAsync(factory);
        await gate.Db.Exams.Where(e => e.Id == exam.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.Title, e => e.Title), Ct);

        var cancel = ActAsync("cancel", exam.Id.ToString(), token);
        await gate.WaitUntilItBlocksAnotherRequestAsync(factory);
        await gate.CommitAsync();
        Assert.Equal(HttpStatusCode.OK, (await cancel).StatusCode);

        using (await AssertProblemAsync(await SendAsync(HttpMethod.Put, $"/api/questions/{question.Id}", token,
                   JsonContent.Create(new { type = "Essay", text = "Too late", marks = 1 })), HttpStatusCode.Conflict, ErrorCodes.ExamNotEditable)) { }
    }

    // ---- Helpers ----

    private static DateTimeOffset InADay() =>
        DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).AddDays(1);

    private static void SetStatus(Exam exam, ExamStatus status)
    {
        exam.Status = status;
        exam.PublishedAt = status == ExamStatus.Draft ? null : exam.StartsAt.AddDays(-7);
    }

    private async Task<(ApplicationUser User, string AccessToken)> InstructorAsync()
    {
        var user = await _api.CreateUserAsync(RoleNames.Instructor);
        return (user, (await _api.LoginAsync(user)).AccessToken);
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? accessToken, HttpContent? content = null)
    {
        var client = factory.CreateClient();
        if (accessToken is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client.SendAsync(new HttpRequestMessage(method, path) { Content = content }, Ct);
    }

    private Task<HttpResponseMessage> ActAsync(string action, string examId, string? accessToken) =>
        SendAsync(HttpMethod.Post, $"/api/exams/{examId}/{action}", accessToken);

    private static async Task AssertRefusedAsync(HttpResponseMessage response, string code, string message)
    {
        using var problem = await AssertProblemAsync(response, HttpStatusCode.Conflict, code);
        Assert.Equal(message, problem.RootElement.GetProperty("detail").GetString());
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

    private async Task<Exam> InsertExamAsync(Guid instructorId, Action<Exam>? change = null)
    {
        var exam = new Exam
        {
            Id = Guid.NewGuid(),
            InstructorId = instructorId,
            Title = "Lifecycle test exam",
            StartsAt = InADay(),
            DurationMinutes = 60,
            WarningLimit = 20,
        };
        change?.Invoke(exam);
        await SaveAsync(db => db.Exams.Add(exam));
        return exam;
    }

    private static Question NewQuestion(Guid examId, int position, QuestionType type) => new()
    {
        Id = Guid.NewGuid(),
        ExamId = examId,
        Type = type,
        Text = $"Question {position}",
        Marks = 1m,
        Position = position,
    };

    // A valid MCQ (first option correct) by default; an Essay when asked.
    private async Task<Question> InsertQuestionAsync(Guid examId, int position, int optionCount = 2, bool essay = false, int correctOptions = 1)
    {
        var question = NewQuestion(examId, position, essay ? QuestionType.Essay : QuestionType.Mcq);
        var options = essay ? [] : Enumerable.Range(1, optionCount).Select(i => ($"Option {i}", i <= correctOptions)).ToArray();
        await InsertAsync(question, options);
        return question;
    }

    private Task InsertAsync(Question question, (string Text, bool IsCorrect)[] options) =>
        SaveAsync(db =>
        {
            db.Questions.Add(question);
            db.QuestionOptions.AddRange(options.Select((o, i) => new QuestionOption
            {
                Id = Guid.NewGuid(), QuestionId = question.Id, Text = o.Text, IsCorrect = o.IsCorrect, Position = i + 1,
            }));
        });

    private async Task SaveAsync(Action<AppDbContext> change)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        change(db);
        await db.SaveChangesAsync(Ct);
    }

    private async Task<Exam?> StoredExamAsync(Guid id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Exams.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, Ct);
    }

    // The row as inserted, down to its row version: nothing rewrote it.
    private async Task AssertUnchangedAsync(Exam expected)
    {
        var stored = await StoredExamAsync(expected.Id);
        Assert.NotNull(stored);
        Assert.Equal(expected.Status, stored.Status);
        Assert.Equal(expected.PublishedAt, stored.PublishedAt);
        Assert.Equal(expected.Title, stored.Title);
        Assert.Equal(expected.StartsAt, stored.StartsAt);
        Assert.Equal(expected.RowVersion, stored.RowVersion);
    }

    // A database transaction opened by the test on its own connection, standing in for another write in progress on
    // the exam.
    private sealed class HeldTransaction : IAsyncDisposable
    {
        private readonly AsyncServiceScope _scope;
        private readonly IDbContextTransaction _transaction;
        private readonly short _sessionId;

        private HeldTransaction(AsyncServiceScope scope, AppDbContext db, IDbContextTransaction transaction, short sessionId)
        {
            _scope = scope;
            Db = db;
            _transaction = transaction;
            _sessionId = sessionId;
        }

        public AppDbContext Db { get; }

        public static async Task<HeldTransaction> BeginAsync(ApiFactory factory)
        {
            var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var transaction = await db.Database.BeginTransactionAsync(Ct);
            var sessionId = (await db.Database.SqlQuery<short>($"SELECT @@SPID AS [Value]").ToListAsync(Ct)).Single();
            return new HeldTransaction(scope, db, transaction, sessionId);
        }

        // Returns once another database session is waiting on a lock held by this transaction.
        public async Task WaitUntilItBlocksAnotherRequestAsync(ApiFactory factory)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var monitor = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var timeout = Stopwatch.StartNew();
            while (true)
            {
                var blocked = (await monitor.Database
                    .SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.dm_exec_requests WHERE blocking_session_id = {_sessionId}")
                    .ToListAsync(Ct)).Single();
                if (blocked > 0)
                    return;
                if (timeout.Elapsed > TimeSpan.FromSeconds(15))
                    throw new TimeoutException("The API request never waited for the test's transaction.");
                await Task.Delay(TimeSpan.FromMilliseconds(20), Ct);
            }
        }

        public Task CommitAsync() => _transaction.CommitAsync(Ct);

        public async ValueTask DisposeAsync()
        {
            await _transaction.DisposeAsync();
            await _scope.DisposeAsync();
        }
    }
}
