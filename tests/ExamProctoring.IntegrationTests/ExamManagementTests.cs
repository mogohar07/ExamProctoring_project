using System.Data.SqlTypes;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
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

// Phase 2 Step 2.2: instructors create, list, read, update and delete their own exams (§7, D25-D30).
// Each test uses its own new instructor, so the exams it sees are exactly the ones it made.
[Collection(ApiCollection.Name)]
public sealed class ExamManagementTests(ApiFactory factory)
{
    private const string ExamsPath = "/api/exams";

    private static readonly string[] ExamFields =
    [
        "id", "title", "description", "startsAt", "durationMinutes", "endsAt", "status", "phase", "warningLimit", "publishedAt",
    ];

    private readonly AuthApi _api = new(factory);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Create ----

    [Fact]
    public async Task Create_Returns201_WithADraft_StoredInUtc_WithTheConfiguredWarningLimit()
    {
        var (instructor, token) = await InstructorAsync();
        var startsAt = InADay();

        var response = await CreateAsync(token, new
        {
            title = "  Midterm  ",
            description = "  Chapters 1-4  ",
            startsAt = Iso(startsAt.ToOffset(TimeSpan.FromHours(2))),
            durationMinutes = 90,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = await JsonAsync(response);
        var exam = json.RootElement;
        Assert.Equal(ExamFields, exam.EnumerateObject().Select(p => p.Name));
        var id = exam.GetProperty("id").GetGuid();
        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal("Midterm", exam.GetProperty("title").GetString());
        Assert.Equal("Chapters 1-4", exam.GetProperty("description").GetString());
        Assert.Equal(startsAt, exam.GetProperty("startsAt").GetDateTimeOffset());
        Assert.Equal(TimeSpan.Zero, exam.GetProperty("startsAt").GetDateTimeOffset().Offset);
        Assert.Equal(90, exam.GetProperty("durationMinutes").GetInt32());
        Assert.Equal(startsAt.AddMinutes(90), exam.GetProperty("endsAt").GetDateTimeOffset());
        Assert.Equal("Draft", exam.GetProperty("status").GetString());
        Assert.Equal("Upcoming", exam.GetProperty("phase").GetString());
        Assert.Equal(20, exam.GetProperty("warningLimit").GetInt32());
        Assert.Equal(JsonValueKind.Null, exam.GetProperty("publishedAt").ValueKind);

        var stored = (await StoredExamAsync(id))!;
        Assert.Equal(instructor.Id, stored.InstructorId);
        Assert.Equal("Midterm", stored.Title);
        Assert.Equal("Chapters 1-4", stored.Description);
        Assert.Equal(startsAt, stored.StartsAt);
        Assert.Equal(TimeSpan.Zero, stored.StartsAt.Offset);
        Assert.Equal(90, stored.DurationMinutes);
        Assert.Equal(ExamStatus.Draft, stored.Status);
        Assert.Equal(20, stored.WarningLimit);
        Assert.Null(stored.PublishedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_WithABlankDescription_StoresNone(string? description)
    {
        var (_, token) = await InstructorAsync();

        var response = await CreateAsync(token, Body(description: description));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = await JsonAsync(response);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("description").ValueKind);
        Assert.Null((await StoredExamAsync(json.RootElement.GetProperty("id").GetGuid()))!.Description);
    }

    [Fact]
    public async Task Create_IgnoresFieldsInstructorsCannotSet()
    {
        var (instructor, token) = await InstructorAsync();
        var otherInstructor = await _api.CreateUserAsync(RoleNames.Instructor);
        var chosenId = Guid.NewGuid();

        var response = await CreateAsync(token, new
        {
            id = chosenId,
            title = "Midterm",
            startsAt = Iso(InADay()),
            durationMinutes = 90,
            status = "Published",
            phase = "Open",
            warningLimit = 5,
            publishedAt = Iso(InADay()),
            instructorId = otherInstructor.Id,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = await JsonAsync(response);
        var id = json.RootElement.GetProperty("id").GetGuid();
        Assert.NotEqual(chosenId, id);
        Assert.Equal("Draft", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(20, json.RootElement.GetProperty("warningLimit").GetInt32());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("publishedAt").ValueKind);

        var stored = (await StoredExamAsync(id))!;
        Assert.Equal(instructor.Id, stored.InstructorId);
        Assert.Equal(ExamStatus.Draft, stored.Status);
        Assert.Equal(20, stored.WarningLimit);
        Assert.Null(stored.PublishedAt);
        Assert.Equal(0, await CountExamsAsync(otherInstructor.Id));
    }

    [Fact]
    public async Task Create_WithAnEmptyBody_Returns400_ForEveryRequiredField_AndCreatesNothing()
    {
        var (instructor, token) = await InstructorAsync();

        using var problem = await AssertProblemAsync(await CreateAsync(token, new { }), HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);

        Assert.Equal(new[] { "durationMinutes", "startsAt", "title" }, ErrorFields(problem).Order());
        Assert.Equal(0, await CountExamsAsync(instructor.Id));
    }

    [Theory]
    [InlineData("""{"title":"","startsAt":"2026-11-01T09:00:00Z","durationMinutes":90}""", "title", "Title is required.")]
    [InlineData("""{"title":"Midterm","description":"","startsAt":"2026-11-01T09:00:00","durationMinutes":90}""", "startsAt",
        "Start time must be a date and time with a time-zone offset, for example 2026-11-01T09:00:00Z or 2026-11-01T11:00:00+02:00.")]
    [InlineData("""{"title":"Midterm","startsAt":"2026-11-01T09:00:00Z","durationMinutes":0}""", "durationMinutes", "Duration must be between 1 and 360 minutes.")]
    [InlineData("""{"title":"Midterm","startsAt":"2026-11-01T09:00:00Z","durationMinutes":361}""", "durationMinutes", "Duration must be between 1 and 360 minutes.")]
    public async Task Create_WithAnInvalidField_Returns400_NamingTheField(string body, string field, string message)
    {
        var (instructor, token) = await InstructorAsync();

        using var problem = await AssertProblemAsync(await SendAsync(HttpMethod.Post, ExamsPath, token, RawJson(body)),
            HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);

        var error = Assert.Single(problem.RootElement.GetProperty("errors").EnumerateObject());
        Assert.Equal(field, error.Name);
        Assert.Equal(message, Assert.Single(error.Value.EnumerateArray()).GetString());
        Assert.Equal(0, await CountExamsAsync(instructor.Id));
    }

    [Theory]
    [InlineData("""{"title":"Midterm","startsAt":20261101,"durationMinutes":90}""")]
    [InlineData("""{"title":"Midterm","startsAt":"2026-11-01T09:00:00Z","durationMinutes":"ninety"}""")]
    [InlineData("""{"title":"Midterm","startsAt":"2026-11-01T09:00:00Z","durationMinutes":1.5}""")]
    [InlineData("""{"title":"Midterm",""")]
    public async Task Create_WithMalformedJson_Returns400(string body)
    {
        var (instructor, token) = await InstructorAsync();

        using var _ = await AssertProblemAsync(await SendAsync(HttpMethod.Post, ExamsPath, token, RawJson(body)),
            HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);

        Assert.Equal(0, await CountExamsAsync(instructor.Id));
    }

    [Fact]
    public async Task Create_StartingNowOrEarlier_Returns400()
    {
        var (instructor, token) = await InstructorAsync();
        var now = InADay();

        using (factory.Clock.FreezeAt(now))
        {
            foreach (var startsAt in new[] { now, now.ToOffset(TimeSpan.FromHours(2)), now.AddSeconds(-1), now.AddDays(-30) })
            {
                using var problem = await AssertProblemAsync(await CreateAsync(token, Body(startsAt: startsAt)),
                    HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
                Assert.Equal("Start time must be in the future.", FieldError(problem, "startsAt"));
            }

            Assert.Equal(0, await CountExamsAsync(instructor.Id));
            Assert.Equal(HttpStatusCode.Created, (await CreateAsync(token, Body(startsAt: now.AddSeconds(1)))).StatusCode);
        }

        Assert.Equal(1, await CountExamsAsync(instructor.Id));
    }

    // ---- Warning limit setting (D26) ----

    [Fact]
    public async Task NewExams_TakeTheConfiguredWarningLimit_AndExistingExamsKeepTheirs()
    {
        var (_, token) = await InstructorAsync();
        var earlier = await CreatedIdAsync(token);

        await using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("Exams:WarningLimit", "5"));
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync(ExamsPath, Body(), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var created = await JsonAsync(response);
        Assert.Equal(5, created.RootElement.GetProperty("warningLimit").GetInt32());
        Assert.Equal(5, (await StoredExamAsync(created.RootElement.GetProperty("id").GetGuid()))!.WarningLimit);

        using var existing = await JsonAsync(await client.GetAsync($"{ExamsPath}/{earlier}", Ct));
        Assert.Equal(20, existing.RootElement.GetProperty("warningLimit").GetInt32());
        Assert.Equal(20, (await StoredExamAsync(earlier))!.WarningLimit);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("21")]
    public void InvalidWarningLimit_PreventsStartup(string value)
    {
        using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("Exams:WarningLimit", value));

        var exception = Record.Exception(() => host.Services);

        Assert.NotNull(exception);
        Assert.Contains("Exams:WarningLimit must be between 1 and 20.", AllMessages(exception));
    }

    // ---- Read ----

    [Fact]
    public async Task Get_ReturnsTheExam_AsCreated()
    {
        var (_, token) = await InstructorAsync();
        var created = await CreateAsync(token, Body());
        var createdJson = await created.Content.ReadAsStringAsync(Ct);
        using var createdExam = JsonDocument.Parse(createdJson);

        var response = await GetExamAsync(token, createdExam.RootElement.GetProperty("id").GetGuid().ToString());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(createdJson, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Get_AnotherInstructorsExam_AnUnknownId_OrNotAnId_Returns404()
    {
        var (_, token) = await InstructorAsync();
        var otherInstructor = await _api.CreateUserAsync(RoleNames.Instructor);
        var othersExam = await InsertExamAsync(otherInstructor.Id);

        foreach (var id in new[] { othersExam.Id.ToString(), Guid.NewGuid().ToString(), "not-an-id", "123" })
            using (await AssertProblemAsync(await GetExamAsync(token, id), HttpStatusCode.NotFound, ErrorCodes.ExamNotFound)) { }
    }

    // ---- Phase (D28) ----

    [Fact]
    public async Task Phase_IsUpcomingBeforeTheStart_OpenUntilTheEnd_ThenClosed()
    {
        var (_, token) = await InstructorAsync();
        var startsAt = InADay();
        var id = await CreatedIdAsync(token, Body(startsAt: startsAt, durationMinutes: 60));

        foreach (var (at, phase) in new[]
                 {
                     (startsAt.AddTicks(-1), "Upcoming"),
                     (startsAt, "Open"),
                     (startsAt.AddMinutes(60).AddTicks(-1), "Open"),
                     (startsAt.AddMinutes(60), "Closed"),
                 })
        {
            using (factory.Clock.FreezeAt(at))
            {
                using var exam = await JsonAsync(await GetExamAsync(token, id.ToString()));
                Assert.Equal(phase, exam.RootElement.GetProperty("phase").GetString());
                Assert.Equal("Draft", exam.RootElement.GetProperty("status").GetString());

                using var list = await JsonAsync(await ListAsync(token));
                Assert.Equal(phase, Assert.Single(list.RootElement.GetProperty("items").EnumerateArray()).GetProperty("phase").GetString());
            }
        }
    }

    [Fact]
    public async Task Phase_IsCalculatedForCancelledExamsToo()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id, e =>
        {
            e.StartsAt = InADay().AddDays(-3);
            e.Status = ExamStatus.Cancelled;
            e.PublishedAt = e.StartsAt.AddDays(-7);
        });

        using var json = await JsonAsync(await GetExamAsync(token, exam.Id.ToString()));

        Assert.Equal("Cancelled", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("Closed", json.RootElement.GetProperty("phase").GetString());
        Assert.Equal(exam.PublishedAt, json.RootElement.GetProperty("publishedAt").GetDateTimeOffset());
    }

    // ---- List (D29) ----

    [Fact]
    public async Task List_ReturnsOnlyTheCallersExams_NewestStartFirst_TenPerPageByDefault()
    {
        var (instructor, token) = await InstructorAsync();
        var otherInstructor = await _api.CreateUserAsync(RoleNames.Instructor);
        await InsertExamAsync(otherInstructor.Id);
        var start = InADay();
        var ids = new List<Guid>();
        for (var i = 0; i < 12; i++)
        {
            var startsAt = start.AddHours(i);
            ids.Add((await InsertExamAsync(instructor.Id, e => e.StartsAt = startsAt)).Id);
        }

        var newestFirst = Enumerable.Reverse(ids).ToList();

        using (var json = await JsonAsync(await ListAsync(token)))
        {
            var root = json.RootElement;
            Assert.Equal(new[] { "items", "page", "pageSize", "totalCount" }, root.EnumerateObject().Select(p => p.Name));
            Assert.Equal(1, root.GetProperty("page").GetInt32());
            Assert.Equal(10, root.GetProperty("pageSize").GetInt32());
            Assert.Equal(12, root.GetProperty("totalCount").GetInt32());
            Assert.Equal(newestFirst.Take(10), Ids(root));
            Assert.All(root.GetProperty("items").EnumerateArray(), item => Assert.Equal(ExamFields, item.EnumerateObject().Select(p => p.Name)));
        }

        await AssertPageAsync(token, "?page=2", newestFirst.Skip(10), page: 2, pageSize: 10, totalCount: 12);
        await AssertPageAsync(token, "?page=3", [], page: 3, pageSize: 10, totalCount: 12);
        await AssertPageAsync(token, "?pageSize=100", newestFirst, page: 1, pageSize: 100, totalCount: 12);
        await AssertPageAsync(token, "?page=2&pageSize=5", newestFirst.Skip(5).Take(5), page: 2, pageSize: 5, totalCount: 12);
        await AssertPageAsync(token, $"?page={int.MaxValue}&pageSize=100", [], page: int.MaxValue, pageSize: 100, totalCount: 12);
    }

    [Fact]
    public async Task List_WithNoExams_ReturnsAnEmptyPage()
    {
        var (_, token) = await InstructorAsync();

        await AssertPageAsync(token, "", [], page: 1, pageSize: 10, totalCount: 0);
    }

    [Fact]
    public async Task List_FiltersByStatus_InAnyLetterCase_AndShowsCancelledExams()
    {
        var (instructor, token) = await InstructorAsync();
        var start = InADay();
        var draft = await InsertExamAsync(instructor.Id, e => e.StartsAt = start);
        var published = await InsertExamAsync(instructor.Id, e => Publish(e, start.AddHours(1)));
        var cancelled = await InsertExamAsync(instructor.Id, e =>
        {
            Publish(e, start.AddHours(2));
            e.Status = ExamStatus.Cancelled;
        });

        await AssertPageAsync(token, "?status=draft", [draft.Id], page: 1, pageSize: 10, totalCount: 1);
        await AssertPageAsync(token, "?status=PUBLISHED", [published.Id], page: 1, pageSize: 10, totalCount: 1);
        await AssertPageAsync(token, "?status=Cancelled", [cancelled.Id], page: 1, pageSize: 10, totalCount: 1);
        await AssertPageAsync(token, "", [cancelled.Id, published.Id, draft.Id], page: 1, pageSize: 10, totalCount: 3);
    }

    [Fact]
    public async Task List_ExamsWithTheSameStartTime_AreAlwaysInTheSameOrder()
    {
        var (instructor, token) = await InstructorAsync();
        var startsAt = InADay();
        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++)
            ids.Add((await InsertExamAsync(instructor.Id, e => e.StartsAt = startsAt)).Id);

        // By id, as SQL Server orders uniqueidentifier values.
        var expected = ids.OrderByDescending(id => new SqlGuid(id)).ToList();

        var pageByPage = new List<Guid>();
        for (var page = 1; page <= 4; page++)
        {
            using var json = await JsonAsync(await ListAsync(token, $"?page={page}&pageSize=1"));
            pageByPage.AddRange(Ids(json.RootElement));
        }

        Assert.Equal(expected, pageByPage);
    }

    [Theory]
    [InlineData("?status=Open", "status", "Status must be Draft, Published or Cancelled.")]
    [InlineData("?status=1", "status", "Status must be Draft, Published or Cancelled.")]
    [InlineData("?page=0", "page", "Page must be 1 or greater.")]
    [InlineData("?page=-3", "page", "Page must be 1 or greater.")]
    [InlineData("?pageSize=0", "pageSize", "Page size must be between 1 and 100.")]
    [InlineData("?pageSize=101", "pageSize", "Page size must be between 1 and 100.")]
    public async Task List_WithAnInvalidParameter_Returns400(string query, string field, string message)
    {
        var (_, token) = await InstructorAsync();

        using var problem = await AssertProblemAsync(await ListAsync(token, query), HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);

        Assert.Equal(message, FieldError(problem, field));
    }

    [Theory]
    [InlineData("?page=abc")]
    [InlineData("?pageSize=1.5")]
    [InlineData("?page=99999999999")]
    public async Task List_WithAParameterThatIsNotANumber_Returns400(string query)
    {
        var (_, token) = await InstructorAsync();

        using var _ = await AssertProblemAsync(await ListAsync(token, query), HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
    }

    // ---- Update (D25, D27) ----

    [Fact]
    public async Task Update_ADraft_BeforeItStarts_ReplacesTheEditableFields_Only()
    {
        var (instructor, token) = await InstructorAsync();
        var id = await CreatedIdAsync(token);
        var newStart = InADay().AddDays(2);

        var response = await UpdateAsync(token, id.ToString(), new
        {
            title = "  Final  ",
            description = (string?)null,
            startsAt = Iso(newStart.ToOffset(TimeSpan.FromHours(-5))),
            durationMinutes = 120,
            status = "Published",
            warningLimit = 3,
            publishedAt = Iso(InADay()),
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonAsync(response);
        var exam = json.RootElement;
        Assert.Equal(ExamFields, exam.EnumerateObject().Select(p => p.Name));
        Assert.Equal(id, exam.GetProperty("id").GetGuid());
        Assert.Equal("Final", exam.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, exam.GetProperty("description").ValueKind);
        Assert.Equal(newStart, exam.GetProperty("startsAt").GetDateTimeOffset());
        Assert.Equal(TimeSpan.Zero, exam.GetProperty("startsAt").GetDateTimeOffset().Offset);
        Assert.Equal(120, exam.GetProperty("durationMinutes").GetInt32());
        Assert.Equal(newStart.AddMinutes(120), exam.GetProperty("endsAt").GetDateTimeOffset());
        Assert.Equal("Draft", exam.GetProperty("status").GetString());
        Assert.Equal("Upcoming", exam.GetProperty("phase").GetString());
        Assert.Equal(20, exam.GetProperty("warningLimit").GetInt32());
        Assert.Equal(JsonValueKind.Null, exam.GetProperty("publishedAt").ValueKind);

        var stored = (await StoredExamAsync(id))!;
        Assert.Equal(instructor.Id, stored.InstructorId);
        Assert.Equal("Final", stored.Title);
        Assert.Null(stored.Description);
        Assert.Equal(newStart, stored.StartsAt);
        Assert.Equal(TimeSpan.Zero, stored.StartsAt.Offset);
        Assert.Equal(120, stored.DurationMinutes);
        Assert.Equal(ExamStatus.Draft, stored.Status);
        Assert.Equal(20, stored.WarningLimit);
        Assert.Null(stored.PublishedAt);
    }

    [Fact]
    public async Task Update_APublishedExam_BeforeItStarts_KeepsItPublished()
    {
        var (instructor, token) = await InstructorAsync();
        var startsAt = InADay();
        var exam = await InsertExamAsync(instructor.Id, e => Publish(e, startsAt));

        var response = await UpdateAsync(token, exam.Id.ToString(), Body(title: "Renamed", startsAt: startsAt, durationMinutes: 45));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonAsync(response);
        Assert.Equal("Published", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(exam.PublishedAt, json.RootElement.GetProperty("publishedAt").GetDateTimeOffset());

        var stored = (await StoredExamAsync(exam.Id))!;
        Assert.Equal("Renamed", stored.Title);
        Assert.Equal(45, stored.DurationMinutes);
        Assert.Equal(ExamStatus.Published, stored.Status);
        Assert.Equal(exam.PublishedAt, stored.PublishedAt);
    }

    [Fact]
    public async Task Update_MovingTheStartToNowOrEarlier_Returns400_ButAnEarlierFutureStartIsAllowed()
    {
        var (instructor, token) = await InstructorAsync();
        var now = InADay();
        var exam = await InsertExamAsync(instructor.Id, e => e.StartsAt = now.AddHours(2));

        using (factory.Clock.FreezeAt(now))
        {
            foreach (var startsAt in new[] { now, now.AddMinutes(-1), now.AddDays(-1) })
            {
                using var problem = await AssertProblemAsync(await UpdateAsync(token, exam.Id.ToString(), Body(startsAt: startsAt)),
                    HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
                Assert.Equal("Start time must be in the future.", FieldError(problem, "startsAt"));
            }

            await AssertUnchangedAsync(exam);

            Assert.Equal(HttpStatusCode.OK, (await UpdateAsync(token, exam.Id.ToString(), Body(startsAt: now.AddSeconds(1)))).StatusCode);
        }

        Assert.Equal(now.AddSeconds(1), (await StoredExamAsync(exam.Id))!.StartsAt);
    }

    [Fact]
    public async Task Update_JustBeforeTheStart_Succeeds()
    {
        var (instructor, token) = await InstructorAsync();
        var startsAt = InADay();
        var exam = await InsertExamAsync(instructor.Id, e => e.StartsAt = startsAt);

        using (factory.Clock.FreezeAt(startsAt.AddTicks(-1)))
        {
            Assert.Equal(HttpStatusCode.OK,
                (await UpdateAsync(token, exam.Id.ToString(), Body(title: "Last minute", startsAt: startsAt))).StatusCode);
        }

        Assert.Equal("Last minute", (await StoredExamAsync(exam.Id))!.Title);
    }

    [Theory]
    [InlineData(ExamStatus.Draft, 0)]
    [InlineData(ExamStatus.Draft, 30)]
    [InlineData(ExamStatus.Draft, 600)]
    [InlineData(ExamStatus.Published, 0)]
    [InlineData(ExamStatus.Published, 30)]
    [InlineData(ExamStatus.Published, 600)]
    public async Task Update_FromTheStartTimeOn_Returns409_AndChangesNothing(ExamStatus status, int minutesAfterStart)
    {
        var (instructor, token) = await InstructorAsync();
        var startsAt = InADay();
        var exam = await InsertExamAsync(instructor.Id, e =>
        {
            e.StartsAt = startsAt;
            if (status == ExamStatus.Published)
                Publish(e, startsAt);
        });

        using (factory.Clock.FreezeAt(startsAt.AddMinutes(minutesAfterStart)))
        {
            using var _ = await AssertProblemAsync(
                await UpdateAsync(token, exam.Id.ToString(), Body(startsAt: startsAt.AddDays(7))),
                HttpStatusCode.Conflict, ErrorCodes.ExamNotEditable);
        }

        await AssertUnchangedAsync(exam);
    }

    [Fact]
    public async Task Update_ACancelledExam_Returns409_AndChangesNothing()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id, e =>
        {
            Publish(e, InADay());
            e.Status = ExamStatus.Cancelled;
        });

        using var _ = await AssertProblemAsync(await UpdateAsync(token, exam.Id.ToString(), Body()),
            HttpStatusCode.Conflict, ErrorCodes.ExamNotEditable);

        await AssertUnchangedAsync(exam);
    }

    [Fact]
    public async Task Update_WithAnInvalidField_Returns400_AndChangesNothing()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id);

        using var problem = await AssertProblemAsync(await UpdateAsync(token, exam.Id.ToString(), Body(durationMinutes: 361)),
            HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);

        Assert.Equal("Duration must be between 1 and 360 minutes.", FieldError(problem, "durationMinutes"));
        await AssertUnchangedAsync(exam);
    }

    [Fact]
    public async Task Update_AnotherInstructorsExam_AnUnknownId_OrNotAnId_Returns404()
    {
        var (_, token) = await InstructorAsync();
        var otherInstructor = await _api.CreateUserAsync(RoleNames.Instructor);
        var othersExam = await InsertExamAsync(otherInstructor.Id);

        foreach (var id in new[] { othersExam.Id.ToString(), Guid.NewGuid().ToString(), "not-an-id" })
            using (await AssertProblemAsync(await UpdateAsync(token, id, Body(title: "Taken over")), HttpStatusCode.NotFound, ErrorCodes.ExamNotFound)) { }

        await AssertUnchangedAsync(othersExam);
    }

    // ---- Delete (D30) ----

    [Fact]
    public async Task Delete_ADraft_Returns204_AndRemovesItsQuestionsAndOptions()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id);
        var question = new Question { Id = Guid.NewGuid(), ExamId = exam.Id, Type = QuestionType.Mcq, Text = "2 + 2?", Marks = 1m, Position = 1 };
        await SaveAsync(db =>
        {
            db.Questions.Add(question);
            db.QuestionOptions.Add(new QuestionOption { Id = Guid.NewGuid(), QuestionId = question.Id, Text = "4", IsCorrect = true, Position = 1 });
            db.QuestionOptions.Add(new QuestionOption { Id = Guid.NewGuid(), QuestionId = question.Id, Text = "5", Position = 2 });
        });

        var response = await DeleteAsync(token, exam.Id.ToString());

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(Ct));
        Assert.Null(await StoredExamAsync(exam.Id));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.Questions.AnyAsync(q => q.ExamId == exam.Id, Ct));
            Assert.False(await db.QuestionOptions.AnyAsync(o => o.QuestionId == question.Id, Ct));
        }

        using (await AssertProblemAsync(await GetExamAsync(token, exam.Id.ToString()), HttpStatusCode.NotFound, ErrorCodes.ExamNotFound)) { }
        using (await AssertProblemAsync(await DeleteAsync(token, exam.Id.ToString()), HttpStatusCode.NotFound, ErrorCodes.ExamNotFound)) { }
    }

    [Fact]
    public async Task Delete_ADraftWhoseStartTimeHasPassed_Returns204()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id, e => e.StartsAt = InADay().AddDays(-3));

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(token, exam.Id.ToString())).StatusCode);
        Assert.Null(await StoredExamAsync(exam.Id));
    }

    [Theory]
    [InlineData(ExamStatus.Published, false)]
    [InlineData(ExamStatus.Published, true)]
    [InlineData(ExamStatus.Cancelled, false)]
    [InlineData(ExamStatus.Cancelled, true)]
    public async Task Delete_APublishedOrCancelledExam_Returns409_AndKeepsIt(ExamStatus status, bool started)
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id, e =>
        {
            Publish(e, started ? InADay().AddDays(-3) : InADay());
            e.Status = status;
        });

        using var _ = await AssertProblemAsync(await DeleteAsync(token, exam.Id.ToString()), HttpStatusCode.Conflict, ErrorCodes.ExamNotEditable);

        await AssertUnchangedAsync(exam);
    }

    [Fact]
    public async Task Delete_AnotherInstructorsExam_AnUnknownId_OrNotAnId_Returns404()
    {
        var (_, token) = await InstructorAsync();
        var otherInstructor = await _api.CreateUserAsync(RoleNames.Instructor);
        var othersExam = await InsertExamAsync(otherInstructor.Id);

        foreach (var id in new[] { othersExam.Id.ToString(), Guid.NewGuid().ToString(), "not-an-id" })
            using (await AssertProblemAsync(await DeleteAsync(token, id), HttpStatusCode.NotFound, ErrorCodes.ExamNotFound)) { }

        await AssertUnchangedAsync(othersExam);
    }

    // ---- Authorization ----

    public static TheoryData<string, string> EndpointsByOtherRoles()
    {
        var data = new TheoryData<string, string>();
        foreach (var endpoint in Endpoints)
        {
            data.Add(endpoint, RoleNames.Student);
            data.Add(endpoint, RoleNames.Admin);
        }

        return data;
    }

    public static TheoryData<string> AllEndpoints() => new(Endpoints);

    [Theory]
    [MemberData(nameof(EndpointsByOtherRoles))]
    public async Task StudentsAndAdmins_Get403_AndChangeNothing(string endpoint, string role)
    {
        var (instructor, _) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id);
        var caller = await _api.CreateUserAsync(role);
        var session = await _api.LoginAsync(caller);

        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(endpoint, exam.Id, session.AccessToken)).StatusCode);

        await AssertUnchangedAsync(exam);
        Assert.Equal(0, await CountExamsAsync(caller.Id));
    }

    [Theory]
    [MemberData(nameof(AllEndpoints))]
    public async Task WithoutAToken_Returns401TokenInvalid(string endpoint)
    {
        var (instructor, _) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id);

        using var _ = await AssertProblemAsync(await CallAsync(endpoint, exam.Id, accessToken: null),
            HttpStatusCode.Unauthorized, ErrorCodes.TokenInvalid);

        await AssertUnchangedAsync(exam);
    }

    [Theory]
    [MemberData(nameof(AllEndpoints))]
    public async Task AnInstructorWhoMustChangeTheirPassword_Gets403PasswordChangeRequired(string endpoint)
    {
        var instructor = await _api.CreateUserAsync(RoleNames.Instructor, mustChangePassword: true,
            temporaryPasswordExpiresAt: DateTimeOffset.UtcNow.AddHours(72));
        var session = await _api.LoginAsync(instructor);
        var exam = await InsertExamAsync(instructor.Id);

        using var _ = await AssertProblemAsync(await CallAsync(endpoint, exam.Id, session.AccessToken),
            HttpStatusCode.Forbidden, ErrorCodes.PasswordChangeRequired);

        await AssertUnchangedAsync(exam);
        Assert.Equal(1, await CountExamsAsync(instructor.Id));
    }

    // ---- Writes to the same exam at the same moment (Phase 2 plan, R2) ----

    [Fact]
    public async Task UpdateDuringACancellation_Returns409_AndChangesNothing()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id, e => Publish(e, InADay()));

        // A cancellation has locked the exam and marked it Cancelled, but has not committed yet.
        await using var cancellation = await HeldTransaction.BeginAsync(factory);
        await cancellation.Db.Exams.Where(e => e.Id == exam.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, ExamStatus.Cancelled), Ct);

        var update = UpdateAsync(token, exam.Id.ToString(), Body(title: "Too late"));
        await cancellation.WaitUntilItBlocksAnotherRequestAsync(factory);
        await cancellation.CommitAsync();

        using var _ = await AssertProblemAsync(await update, HttpStatusCode.Conflict, ErrorCodes.ExamNotEditable);
        var stored = (await StoredExamAsync(exam.Id))!;
        Assert.Equal(ExamStatus.Cancelled, stored.Status);
        Assert.Equal(exam.Title, stored.Title);
    }

    [Fact]
    public async Task DeleteDuringAPublication_Returns409_AndKeepsTheExam()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id);
        var publishedAt = DateTimeOffset.UtcNow;

        // A publication has locked the exam and marked it Published, but has not committed yet.
        await using var publication = await HeldTransaction.BeginAsync(factory);
        await publication.Db.Exams.Where(e => e.Id == exam.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, ExamStatus.Published).SetProperty(e => e.PublishedAt, publishedAt), Ct);

        var delete = DeleteAsync(token, exam.Id.ToString());
        await publication.WaitUntilItBlocksAnotherRequestAsync(factory);
        await publication.CommitAsync();

        using var _ = await AssertProblemAsync(await delete, HttpStatusCode.Conflict, ErrorCodes.ExamNotEditable);
        var stored = (await StoredExamAsync(exam.Id))!;
        Assert.Equal(ExamStatus.Published, stored.Status);
        Assert.Equal(publishedAt, stored.PublishedAt);
    }

    [Fact]
    public async Task UpdateDuringAnotherUpdate_WaitsForIt_AndTheLaterOneWins()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id);

        await using var firstUpdate = await HeldTransaction.BeginAsync(factory);
        await firstUpdate.Db.Exams.Where(e => e.Id == exam.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Title, "First").SetProperty(e => e.DurationMinutes, 30), Ct);

        var secondUpdate = UpdateAsync(token, exam.Id.ToString(), Body(title: "Second", startsAt: exam.StartsAt, durationMinutes: 45));
        await firstUpdate.WaitUntilItBlocksAnotherRequestAsync(factory);
        await firstUpdate.CommitAsync();

        Assert.Equal(HttpStatusCode.OK, (await secondUpdate).StatusCode);
        var stored = (await StoredExamAsync(exam.Id))!;
        Assert.Equal("Second", stored.Title);
        Assert.Equal(45, stored.DurationMinutes);
    }

    [Fact]
    public async Task UpdateDuringADelete_Returns404()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id);

        await using var deletion = await HeldTransaction.BeginAsync(factory);
        await deletion.Db.Exams.Where(e => e.Id == exam.Id).ExecuteDeleteAsync(Ct);

        var update = UpdateAsync(token, exam.Id.ToString(), Body(title: "Gone"));
        await deletion.WaitUntilItBlocksAnotherRequestAsync(factory);
        await deletion.CommitAsync();

        using var _ = await AssertProblemAsync(await update, HttpStatusCode.NotFound, ErrorCodes.ExamNotFound);
        Assert.Null(await StoredExamAsync(exam.Id));
    }

    // ---- Helpers ----

    private static readonly string[] Endpoints = ["list", "create", "get", "update", "delete"];

    // Whole seconds a day from now, in UTC, so a time sent as text compares equal to the stored one.
    private static DateTimeOffset InADay() =>
        DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).AddDays(1);

    private static string Iso(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture);

    private static object Body(
        string title = "Midterm", string? description = "Chapters 1-4", DateTimeOffset? startsAt = null, int durationMinutes = 90) =>
        new { title, description, startsAt = Iso(startsAt ?? InADay()), durationMinutes };

    private static void Publish(Exam exam, DateTimeOffset startsAt)
    {
        exam.StartsAt = startsAt;
        exam.Status = ExamStatus.Published;
        exam.PublishedAt = startsAt.AddDays(-7);
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

    private static StringContent RawJson(string json) => new(json, Encoding.UTF8, "application/json");

    private Task<HttpResponseMessage> CreateAsync(string accessToken, object body) =>
        SendAsync(HttpMethod.Post, ExamsPath, accessToken, JsonContent.Create(body));

    private Task<HttpResponseMessage> ListAsync(string accessToken, string query = "") =>
        SendAsync(HttpMethod.Get, ExamsPath + query, accessToken);

    private Task<HttpResponseMessage> GetExamAsync(string accessToken, string id) =>
        SendAsync(HttpMethod.Get, $"{ExamsPath}/{id}", accessToken);

    private Task<HttpResponseMessage> UpdateAsync(string accessToken, string id, object body) =>
        SendAsync(HttpMethod.Put, $"{ExamsPath}/{id}", accessToken, JsonContent.Create(body));

    private Task<HttpResponseMessage> DeleteAsync(string accessToken, string id) =>
        SendAsync(HttpMethod.Delete, $"{ExamsPath}/{id}", accessToken);

    private Task<HttpResponseMessage> CallAsync(string endpoint, Guid examId, string? accessToken) => endpoint switch
    {
        "list" => SendAsync(HttpMethod.Get, ExamsPath, accessToken),
        "create" => SendAsync(HttpMethod.Post, ExamsPath, accessToken, JsonContent.Create(Body())),
        "get" => SendAsync(HttpMethod.Get, $"{ExamsPath}/{examId}", accessToken),
        "update" => SendAsync(HttpMethod.Put, $"{ExamsPath}/{examId}", accessToken, JsonContent.Create(Body(title: "Changed"))),
        "delete" => SendAsync(HttpMethod.Delete, $"{ExamsPath}/{examId}", accessToken),
        _ => throw new ArgumentOutOfRangeException(nameof(endpoint)),
    };

    private async Task<Guid> CreatedIdAsync(string accessToken, object? body = null)
    {
        var response = await CreateAsync(accessToken, body ?? Body());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = await JsonAsync(response);
        return json.RootElement.GetProperty("id").GetGuid();
    }

    private async Task AssertPageAsync(string accessToken, string query, IEnumerable<Guid> expectedIds, int page, int pageSize, int totalCount)
    {
        var response = await ListAsync(accessToken, query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonAsync(response);
        Assert.Equal(expectedIds, Ids(json.RootElement));
        Assert.Equal(page, json.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(pageSize, json.RootElement.GetProperty("pageSize").GetInt32());
        Assert.Equal(totalCount, json.RootElement.GetProperty("totalCount").GetInt32());
    }

    private static List<Guid> Ids(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToList();

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

    private static IEnumerable<string> ErrorFields(JsonDocument problem) =>
        problem.RootElement.GetProperty("errors").EnumerateObject().Select(p => p.Name);

    private static string? FieldError(JsonDocument problem, string field) =>
        Assert.Single(problem.RootElement.GetProperty("errors").GetProperty(field).EnumerateArray()).GetString();

    private async Task<Exam> InsertExamAsync(Guid instructorId, Action<Exam>? change = null)
    {
        var exam = new Exam
        {
            Id = Guid.NewGuid(),
            InstructorId = instructorId,
            Title = "Stored exam",
            Description = "Stored description",
            StartsAt = InADay(),
            DurationMinutes = 60,
            WarningLimit = 20,
        };
        change?.Invoke(exam);
        await SaveAsync(db => db.Exams.Add(exam));
        return exam;
    }

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
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Exams.AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == id, Ct);
    }

    private async Task<int> CountExamsAsync(Guid instructorId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Exams.CountAsync(e => e.InstructorId == instructorId, Ct);
    }

    // The row as inserted: same values and the same row version, so it was not even rewritten.
    private async Task AssertUnchangedAsync(Exam expected)
    {
        var stored = await StoredExamAsync(expected.Id);
        Assert.NotNull(stored);
        Assert.Equal(expected.InstructorId, stored.InstructorId);
        Assert.Equal(expected.Title, stored.Title);
        Assert.Equal(expected.Description, stored.Description);
        Assert.Equal(expected.StartsAt, stored.StartsAt);
        Assert.Equal(expected.DurationMinutes, stored.DurationMinutes);
        Assert.Equal(expected.Status, stored.Status);
        Assert.Equal(expected.WarningLimit, stored.WarningLimit);
        Assert.Equal(expected.PublishedAt, stored.PublishedAt);
        Assert.Equal(expected.RowVersion, stored.RowVersion);
    }

    private static string AllMessages(Exception exception)
    {
        var messages = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
            if (current is AggregateException aggregate)
                messages.AddRange(aggregate.InnerExceptions.Select(e => e.Message));
        }

        return string.Join(Environment.NewLine, messages);
    }

    // A database transaction opened by the test on its own connection, standing in for another write in progress on
    // the exam (such as a publication or cancellation from Step 2.4).
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
