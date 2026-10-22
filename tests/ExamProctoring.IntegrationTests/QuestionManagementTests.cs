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
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using static ExamProctoring.IntegrationTests.TestSupport.AuthApi;

namespace ExamProctoring.IntegrationTests;

// Phase 2 Step 2.3: questions of the instructor's own exams (§7, D27, D31-D35). Each test uses its own new instructor
// and exams, so the questions it sees are exactly the ones it made.
[Collection(ApiCollection.Name)]
public sealed class QuestionManagementTests(ApiFactory factory)
{
    private static readonly string[] QuestionFields = ["id", "examId", "type", "text", "marks", "modelAnswer", "position", "options"];

    private static readonly string[] OptionFields = ["id", "text", "isCorrect", "position"];

    private static readonly string[] Endpoints = ["list", "create", "update", "delete"];

    private readonly AuthApi _api = new(factory);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Create ----

    [Fact]
    public async Task Create_Mcq_Returns201_WithTheQuestion_AndStoresIt()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id);

        var response = await CreateAsync(token, exam.Id, new
        {
            type = "mcq",
            text = "  Capital of France?  ",
            marks = 2.5m,
            modelAnswer = "  Paris is the capital.  ",
            options = new object[]
            {
                new { text = " Paris ", isCorrect = true },
                new { text = "Lyon", isCorrect = false },
                new { text = "Nice" },
            },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = await JsonAsync(response);
        var question = json.RootElement;
        Assert.Equal(QuestionFields, question.EnumerateObject().Select(p => p.Name));
        var id = question.GetProperty("id").GetGuid();
        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal(exam.Id, question.GetProperty("examId").GetGuid());
        Assert.Equal("Mcq", question.GetProperty("type").GetString());
        Assert.Equal("Capital of France?", question.GetProperty("text").GetString());
        Assert.Equal("2.50", question.GetProperty("marks").GetRawText());
        Assert.Equal("Paris is the capital.", question.GetProperty("modelAnswer").GetString());
        Assert.Equal(1, question.GetProperty("position").GetInt32());

        var options = question.GetProperty("options").EnumerateArray().ToList();
        Assert.All(options, option => Assert.Equal(OptionFields, option.EnumerateObject().Select(p => p.Name)));
        Assert.Equal(["Paris", "Lyon", "Nice"], options.Select(o => o.GetProperty("text").GetString()));
        Assert.Equal([true, false, false], options.Select(o => o.GetProperty("isCorrect").GetBoolean()));
        Assert.Equal([1, 2, 3], options.Select(o => o.GetProperty("position").GetInt32()));
        Assert.Equal(3, options.Select(o => o.GetProperty("id").GetGuid()).Distinct().Count(g => g != Guid.Empty));

        var stored = Assert.Single(await StoredQuestionsAsync(exam.Id));
        Assert.Equal(id, stored.Question.Id);
        Assert.Equal(QuestionType.Mcq, stored.Question.Type);
        Assert.Equal("Capital of France?", stored.Question.Text);
        Assert.Equal(2.5m, stored.Question.Marks);
        Assert.Equal("Paris is the capital.", stored.Question.ModelAnswer);
        Assert.Equal(1, stored.Question.Position);
        Assert.Equal(options.Select(o => o.GetProperty("id").GetGuid()), stored.Options.Select(o => o.Id));
        Assert.Equal(["Paris", "Lyon", "Nice"], stored.Options.Select(o => o.Text));
        Assert.Equal([true, false, false], stored.Options.Select(o => o.IsCorrect));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_Essay_HasNoOptions_AndABlankModelAnswerIsStoredAsNone(string? modelAnswer)
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id);

        var response = await CreateAsync(token, exam.Id, new { type = "Essay", text = "Explain photosynthesis.", marks = 10, modelAnswer });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = await JsonAsync(response);
        Assert.Equal("Essay", json.RootElement.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("modelAnswer").ValueKind);
        Assert.Equal("10.00", json.RootElement.GetProperty("marks").GetRawText());
        Assert.Equal(JsonValueKind.Array, json.RootElement.GetProperty("options").ValueKind);
        Assert.Empty(json.RootElement.GetProperty("options").EnumerateArray());

        var stored = Assert.Single(await StoredQuestionsAsync(exam.Id));
        Assert.Null(stored.Question.ModelAnswer);
        Assert.Empty(stored.Options);
    }

    [Fact]
    public async Task Create_Essay_WithAnEmptyOptionList_IsAccepted()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id);

        var response = await CreateAsync(token, exam.Id, new { type = "Essay", text = "Explain.", marks = 5, options = Array.Empty<object>() });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Empty(Assert.Single(await StoredQuestionsAsync(exam.Id)).Options);
    }

    [Fact]
    public async Task Create_AppendsEachQuestionAfterTheLast()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id);

        var positions = new List<int>();
        foreach (var text in new[] { "First", "Second", "Third" })
        {
            using var json = await JsonAsync(await CreateAsync(token, exam.Id, Mcq(text: text)));
            positions.Add(json.RootElement.GetProperty("position").GetInt32());
        }

        Assert.Equal([1, 2, 3], positions);
        Assert.Equal(["First", "Second", "Third"], (await StoredQuestionsAsync(exam.Id)).Select(q => q.Question.Text));
    }

    [Fact]
    public async Task Create_OnAPublishedExamBeforeItStarts_Succeeds_AndKeepsItPublished()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id, Publish);

        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(token, exam.Id, Essay())).StatusCode);

        Assert.Equal(ExamStatus.Published, (await StoredExamAsync(exam.Id))!.Status);
    }

    [Fact]
    public async Task Create_AcceptsEveryBoundaryValue()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id);

        var longest = await CreateAsync(token, exam.Id, new
        {
            type = "Mcq",
            text = new string('q', 4000),
            marks = 9999.99m,
            modelAnswer = new string('a', 4000),
            options = new object[] { new { text = new string('o', 1000), isCorrect = true }, new { text = "Short", isCorrect = false } },
        });
        var smallest = await CreateAsync(token, exam.Id, Mcq(marks: 0.01m, options: [("A", true), ("B", false)]));
        var mostOptions = await CreateAsync(token, exam.Id, Mcq(options: [.. Enumerable.Range(1, 8).Select(i => ($"Option {i}", i == 1))]));
        var trailingZeros = await SendAsync(HttpMethod.Post, QuestionsPath(exam.Id), token,
            RawJson("""{"type":"Essay","text":"Explain.","marks":1.500}"""));

        Assert.Equal(HttpStatusCode.Created, longest.StatusCode);
        Assert.Equal(HttpStatusCode.Created, smallest.StatusCode);
        Assert.Equal(HttpStatusCode.Created, mostOptions.StatusCode);
        Assert.Equal(HttpStatusCode.Created, trailingZeros.StatusCode);
        using var json = await JsonAsync(trailingZeros);
        Assert.Equal("1.50", json.RootElement.GetProperty("marks").GetRawText());
        Assert.Equal([9999.99m, 0.01m, 2m, 1.5m], (await StoredQuestionsAsync(exam.Id)).Select(q => q.Question.Marks));
    }

    [Theory]
    [InlineData("""{"text":"Q","marks":1}""", "type", "Type is required.")]
    [InlineData("""{"type":"TrueFalse","text":"Q","marks":1}""", "type", "Type must be Mcq or Essay.")]
    [InlineData("""{"type":"Essay","text":"   ","marks":1}""", "text", "Text is required.")]
    [InlineData("""{"type":"Essay","text":"Q"}""", "marks", "Marks are required.")]
    [InlineData("""{"type":"Essay","text":"Q","marks":0}""", "marks", "Marks must be greater than 0.")]
    [InlineData("""{"type":"Essay","text":"Q","marks":-2}""", "marks", "Marks must be greater than 0.")]
    [InlineData("""{"type":"Essay","text":"Q","marks":10000}""", "marks", "Marks must be at most 9999.99.")]
    [InlineData("""{"type":"Essay","text":"Q","marks":1.005}""", "marks", "Marks can have at most 2 decimal places.")]
    [InlineData("""{"type":"Mcq","text":"Q","marks":1,"options":[{"text":"A","isCorrect":true}]}""", "options", "An MCQ question needs at least 2 options.")]
    [InlineData("""{"type":"Mcq","text":"Q","marks":1,"options":[{"text":"A","isCorrect":true},{"text":"B","isCorrect":true}]}""", "options", "An MCQ question needs exactly one correct option.")]
    [InlineData("""{"type":"Mcq","text":"Q","marks":1,"options":[{"text":"A"},{"text":"B"}]}""", "options", "An MCQ question needs exactly one correct option.")]
    [InlineData("""{"type":"Mcq","text":"Q","marks":1,"options":[{"text":"Paris","isCorrect":true},{"text":" PARIS ","isCorrect":false}]}""", "options", "Options must not repeat the same text.")]
    [InlineData("""{"type":"Mcq","text":"Q","marks":1,"options":[{"text":"A","isCorrect":true},{"text":"  ","isCorrect":false}]}""", "options[1].text", "Option text is required.")]
    [InlineData("""{"type":"Mcq","text":"Q","marks":1,"options":[{"text":"A","isCorrect":true},{"text":"B"},null]}""", "options[2]", "Option is required.")]
    [InlineData("""{"type":"Essay","text":"Q","marks":1,"options":[{"text":"A","isCorrect":true}]}""", "options", "An Essay question has no options.")]
    public async Task Create_WithAnInvalidField_Returns400_NamingTheField_AndStoresNothing(string body, string field, string message)
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id);

        using var problem = await AssertProblemAsync(await SendAsync(HttpMethod.Post, QuestionsPath(exam.Id), token, RawJson(body)),
            HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);

        var error = Assert.Single(problem.RootElement.GetProperty("errors").EnumerateObject());
        Assert.Equal(field, error.Name);
        Assert.Equal(message, Assert.Single(error.Value.EnumerateArray()).GetString());
        Assert.Empty(await StoredQuestionsAsync(exam.Id));
    }

    [Fact]
    public async Task Create_OverTheTextLimitsOrTheOptionMaximum_Returns400()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id);

        var tooLong = new (object Body, string Field, string Message)[]
        {
            (Mcq(text: new string('q', 4001)), "text", "Text must be at most 4000 characters."),
            (Mcq(modelAnswer: new string('a', 4001)), "modelAnswer", "Model answer must be at most 4000 characters."),
            (Mcq(options: [("A", true), (new string('o', 1001), false)]), "options[1].text", "Option text must be at most 1000 characters."),
            (Mcq(options: [.. Enumerable.Range(1, 9).Select(i => ($"Option {i}", i == 1))]), "options", "An MCQ question can have at most 8 options."),
        };

        foreach (var (body, field, message) in tooLong)
        {
            using var problem = await AssertProblemAsync(await CreateAsync(token, exam.Id, body), HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
            Assert.Equal(message, FieldError(problem, field));
        }

        Assert.Empty(await StoredQuestionsAsync(exam.Id));
    }

    [Theory]
    [InlineData("""{"type":"Essay","text":"Q","marks":"two"}""")]
    [InlineData("""{"type":1,"text":"Q","marks":1}""")]
    [InlineData("""{"type":"Mcq","text":"Q","marks":1,"options":{"text":"A"}}""")]
    [InlineData("""{"type":"Mcq","text":"Q","marks":1,"options":[{"text":"A","isCorrect":"yes"},{"text":"B"}]}""")]
    [InlineData("""{"type":"Essay","text":"Q","marks":1e40}""")]
    [InlineData("""{"type":"Essay",""")]
    public async Task Create_WithMalformedJson_Returns400(string body)
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id);

        using var _ = await AssertProblemAsync(await SendAsync(HttpMethod.Post, QuestionsPath(exam.Id), token, RawJson(body)),
            HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);

        Assert.Empty(await StoredQuestionsAsync(exam.Id));
    }

    [Fact]
    public async Task Create_InAnotherInstructorsExam_AnUnknownExam_OrNotAnId_Returns404()
    {
        var (_, token) = await InstructorAsync();
        var otherInstructor = await _api.CreateUserAsync(RoleNames.Instructor);
        var othersExam = await InsertExamAsync(otherInstructor.Id);

        foreach (var examId in new[] { othersExam.Id.ToString(), Guid.NewGuid().ToString(), "not-an-id" })
            using (await AssertProblemAsync(await SendAsync(HttpMethod.Post, $"/api/exams/{examId}/questions", token, JsonContent.Create(Essay())),
                       HttpStatusCode.NotFound, ErrorCodes.ExamNotFound)) { }

        Assert.Empty(await StoredQuestionsAsync(othersExam.Id));
    }

    // ---- Exam state and time (D27): writes only before the start, Draft or Published; reads always (S4) ----

    [Theory]
    [InlineData(ExamStatus.Draft, 0)]
    [InlineData(ExamStatus.Draft, 30)]
    [InlineData(ExamStatus.Draft, 600)]
    [InlineData(ExamStatus.Published, 0)]
    [InlineData(ExamStatus.Published, 30)]
    [InlineData(ExamStatus.Published, 600)]
    public async Task Writes_FromTheStartTimeOn_Return409_AndChangeNothing(ExamStatus status, int minutesAfterStart)
    {
        var (instructor, token) = await InstructorAsync();
        var startsAt = InADay();
        var exam = await InsertExamAsync(instructor.Id, e =>
        {
            e.StartsAt = startsAt;
            if (status == ExamStatus.Published)
                Publish(e);
        });
        var first = await InsertQuestionAsync(exam.Id, 1);
        await InsertQuestionAsync(exam.Id, 2);
        var before = await SnapshotAsync(exam.Id);

        using (factory.Clock.FreezeAt(startsAt.AddMinutes(minutesAfterStart)))
        {
            await AssertNotEditableAsync(await CreateAsync(token, exam.Id, Essay()));
            await AssertNotEditableAsync(await UpdateAsync(token, first.Id, Essay()));
            await AssertNotEditableAsync(await DeleteAsync(token, first.Id));
        }

        Assert.Equal(before, await SnapshotAsync(exam.Id));
    }

    [Fact]
    public async Task Writes_OnACancelledExam_Return409_AndChangeNothing()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id, e =>
        {
            Publish(e);
            e.Status = ExamStatus.Cancelled;
        });
        var question = await InsertQuestionAsync(exam.Id, 1);
        var before = await SnapshotAsync(exam.Id);

        await AssertNotEditableAsync(await CreateAsync(token, exam.Id, Essay()));
        await AssertNotEditableAsync(await UpdateAsync(token, question.Id, Essay()));
        await AssertNotEditableAsync(await DeleteAsync(token, question.Id));

        Assert.Equal(before, await SnapshotAsync(exam.Id));
    }

    [Fact]
    public async Task Writes_JustBeforeTheStart_Succeed()
    {
        var (instructor, token) = await InstructorAsync();
        var startsAt = InADay();
        var exam = await InsertExamAsync(instructor.Id, e => e.StartsAt = startsAt);
        var question = await InsertQuestionAsync(exam.Id, 1);

        using (factory.Clock.FreezeAt(startsAt.AddTicks(-1)))
        {
            Assert.Equal(HttpStatusCode.Created, (await CreateAsync(token, exam.Id, Essay())).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await UpdateAsync(token, question.Id, Essay(text: "Changed"))).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(token, question.Id)).StatusCode);
        }
    }

    [Theory]
    [InlineData(ExamStatus.Draft, false)]
    [InlineData(ExamStatus.Draft, true)]
    [InlineData(ExamStatus.Published, false)]
    [InlineData(ExamStatus.Published, true)]
    [InlineData(ExamStatus.Cancelled, false)]
    [InlineData(ExamStatus.Cancelled, true)]
    public async Task List_IsAllowed_WhateverTheExamsStatusOrTime(ExamStatus status, bool started)
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id, e =>
        {
            if (status != ExamStatus.Draft)
                Publish(e);
            e.Status = status;
            if (started)
                e.StartsAt = InADay().AddDays(-3);
        });
        var question = await InsertQuestionAsync(exam.Id, 1);

        var response = await ListAsync(token, exam.Id.ToString());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonAsync(response);
        Assert.Equal(question.Id, Assert.Single(json.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
    }

    // ---- List (S2) ----

    [Fact]
    public async Task List_ReturnsEveryQuestion_InPositionOrder_WithOptionsInOrder_NotPaged()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id);
        var otherExam = await InsertExamAsync(instructor.Id);
        await InsertQuestionAsync(otherExam.Id, 1);

        // Stored out of order, with options stored last first.
        var questions = new List<Question>();
        foreach (var position in new[] { 7, 3, 12, 1, 9, 5, 2, 11, 4, 10, 6, 8 })
            questions.Add(await InsertQuestionAsync(exam.Id, position, optionCount: position % 3 == 0 ? 0 : 3, reverseOptions: true));

        var response = await ListAsync(token, exam.Id.ToString());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonAsync(response);
        Assert.Equal(["items"], json.RootElement.EnumerateObject().Select(p => p.Name));
        var items = json.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(Enumerable.Range(1, 12), items.Select(i => i.GetProperty("position").GetInt32()));
        Assert.Equal(questions.OrderBy(q => q.Position).Select(q => q.Id), items.Select(i => i.GetProperty("id").GetGuid()));
        Assert.All(items, item =>
        {
            Assert.Equal(QuestionFields, item.EnumerateObject().Select(p => p.Name));
            Assert.Equal(exam.Id, item.GetProperty("examId").GetGuid());
            var options = item.GetProperty("options").EnumerateArray().ToList();
            Assert.Equal(Enumerable.Range(1, options.Count), options.Select(o => o.GetProperty("position").GetInt32()));
            Assert.Equal(options.Select((_, i) => i == 0), options.Select(o => o.GetProperty("isCorrect").GetBoolean()));
        });
        Assert.Equal(4, items.Count(i => i.GetProperty("options").GetArrayLength() == 0));
    }

    [Fact]
    public async Task List_OfAnExamWithoutQuestions_IsEmpty()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id);

        var response = await ListAsync(token, exam.Id.ToString());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"items":[]}""", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task List_OfAnotherInstructorsExam_AnUnknownExam_OrNotAnId_Returns404()
    {
        var (_, token) = await InstructorAsync();
        var otherInstructor = await _api.CreateUserAsync(RoleNames.Instructor);
        var othersExam = await InsertExamAsync(otherInstructor.Id);
        await InsertQuestionAsync(othersExam.Id, 1);

        foreach (var examId in new[] { othersExam.Id.ToString(), Guid.NewGuid().ToString(), "not-an-id" })
            using (await AssertProblemAsync(await ListAsync(token, examId), HttpStatusCode.NotFound, ErrorCodes.ExamNotFound)) { }
    }

    [Fact]
    public async Task List_ShowsTheQuestionsAsCreatedAndUpdated()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id);
        var created = await (await CreateAsync(token, exam.Id, Mcq())).Content.ReadAsStringAsync(Ct);
        var id = JsonDocument.Parse(created).RootElement.GetProperty("id").GetGuid();
        var updated = await (await UpdateAsync(token, id, Mcq(text: "Changed", options: [("Yes", false), ("No", true)]))).Content.ReadAsStringAsync(Ct);

        var list = await (await ListAsync(token, exam.Id.ToString())).Content.ReadAsStringAsync(Ct);

        Assert.Equal($$"""{"items":[{{updated}}]}""", list);
    }

    // ---- Update (D33) ----

    [Fact]
    public async Task Update_ReplacesTheWholeQuestion_KeepingItsIdExamAndPosition()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id);
        await InsertQuestionAsync(exam.Id, 1);
        var question = await InsertQuestionAsync(exam.Id, 2, optionCount: 4);
        await InsertQuestionAsync(exam.Id, 3);
        var before = await SnapshotAsync(exam.Id);
        var oldOptionIds = (await StoredQuestionsAsync(exam.Id))[1].Options.Select(o => o.Id).ToList();

        var response = await UpdateAsync(token, question.Id, new
        {
            type = "Mcq",
            text = " Which is larger? ",
            marks = 3,
            modelAnswer = (string?)null,
            options = new object[] { new { text = "Two", isCorrect = false }, new { text = "Three", isCorrect = true } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonAsync(response);
        var updated = json.RootElement;
        Assert.Equal(QuestionFields, updated.EnumerateObject().Select(p => p.Name));
        Assert.Equal(question.Id, updated.GetProperty("id").GetGuid());
        Assert.Equal(exam.Id, updated.GetProperty("examId").GetGuid());
        Assert.Equal(2, updated.GetProperty("position").GetInt32());
        Assert.Equal("Which is larger?", updated.GetProperty("text").GetString());
        Assert.Equal("3.00", updated.GetProperty("marks").GetRawText());
        Assert.Equal(JsonValueKind.Null, updated.GetProperty("modelAnswer").ValueKind);

        var stored = await StoredQuestionsAsync(exam.Id);
        Assert.Equal([1, 2, 3], stored.Select(q => q.Question.Position));
        var replaced = stored[1];
        Assert.Equal(question.Id, replaced.Question.Id);
        Assert.Equal(["Two", "Three"], replaced.Options.Select(o => o.Text));
        Assert.Equal([false, true], replaced.Options.Select(o => o.IsCorrect));
        Assert.Equal([1, 2], replaced.Options.Select(o => o.Position));
        Assert.Empty(replaced.Options.Select(o => o.Id).Intersect(oldOptionIds));
        Assert.Equal(updated.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("id").GetGuid()), replaced.Options.Select(o => o.Id));

        // The other questions are untouched, and the old options are gone from the database.
        var after = await SnapshotAsync(exam.Id);
        Assert.Equal(before[0], after[0]);
        Assert.Equal(before[2], after[2]);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().QuestionOptions.AnyAsync(o => oldOptionIds.Contains(o.Id), Ct));
    }

    [Fact]
    public async Task Update_FromMcqToEssay_RemovesTheOptions()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id);
        var question = await InsertQuestionAsync(exam.Id, 1, optionCount: 3);

        var response = await UpdateAsync(token, question.Id, Essay(modelAnswer: "  A model answer.  "));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonAsync(response);
        Assert.Equal("Essay", json.RootElement.GetProperty("type").GetString());
        Assert.Empty(json.RootElement.GetProperty("options").EnumerateArray());
        var stored = Assert.Single(await StoredQuestionsAsync(exam.Id));
        Assert.Equal(QuestionType.Essay, stored.Question.Type);
        Assert.Equal("A model answer.", stored.Question.ModelAnswer);
        Assert.Empty(stored.Options);
    }

    [Fact]
    public async Task Update_FromEssayToMcq_NeedsValidOptions()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id);
        var question = await InsertQuestionAsync(exam.Id, 1, optionCount: 0, type: QuestionType.Essay);
        var before = await SnapshotAsync(exam.Id);

        using (var problem = await AssertProblemAsync(await UpdateAsync(token, question.Id, Mcq(options: [("Only", true)])),
                   HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed))
            Assert.Equal("An MCQ question needs at least 2 options.", FieldError(problem, "options"));
        Assert.Equal(before, await SnapshotAsync(exam.Id));

        Assert.Equal(HttpStatusCode.OK, (await UpdateAsync(token, question.Id, Mcq(options: [("Yes", true), ("No", false)]))).StatusCode);
        var stored = Assert.Single(await StoredQuestionsAsync(exam.Id));
        Assert.Equal(QuestionType.Mcq, stored.Question.Type);
        Assert.Equal(["Yes", "No"], stored.Options.Select(o => o.Text));
    }

    [Theory]
    [InlineData("""{"type":"Mcq","text":"Q","marks":1,"options":[{"text":"Same","isCorrect":true},{"text":"same","isCorrect":false}]}""")]
    [InlineData("""{"type":"Mcq","text":"","marks":1,"options":[{"text":"A","isCorrect":true},{"text":"B","isCorrect":false}]}""")]
    [InlineData("""{"type":"Essay","text":"Q","marks":0.001}""")]
    [InlineData("""{"type":"Essay","text":"Q","marks":1,"options":[{"text":"A","isCorrect":true}]}""")]
    public async Task Update_ThatIsInvalid_ChangesNothing(string body)
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id, Publish);
        var question = await InsertQuestionAsync(exam.Id, 1, optionCount: 3);
        var before = await SnapshotAsync(exam.Id);

        using var _ = await AssertProblemAsync(await SendAsync(HttpMethod.Put, $"/api/questions/{question.Id}", token, RawJson(body)),
            HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);

        Assert.Equal(before, await SnapshotAsync(exam.Id));
    }

    [Fact]
    public async Task Update_OnAPublishedExam_KeepsItPublished()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id, Publish);
        var question = await InsertQuestionAsync(exam.Id, 1);

        Assert.Equal(HttpStatusCode.OK, (await UpdateAsync(token, question.Id, Essay(text: "Now an essay"))).StatusCode);

        var stored = (await StoredExamAsync(exam.Id))!;
        Assert.Equal(ExamStatus.Published, stored.Status);
        Assert.Equal(exam.PublishedAt, stored.PublishedAt);
    }

    [Fact]
    public async Task Update_AnotherInstructorsQuestion_AnUnknownId_OrNotAnId_Returns404()
    {
        var (_, token) = await InstructorAsync();
        var otherInstructor = await _api.CreateUserAsync(RoleNames.Instructor);
        var othersExam = await InsertExamAsync(otherInstructor.Id);
        var othersQuestion = await InsertQuestionAsync(othersExam.Id, 1);
        var before = await SnapshotAsync(othersExam.Id);

        foreach (var id in new[] { othersQuestion.Id.ToString(), Guid.NewGuid().ToString(), "not-an-id" })
            using (await AssertProblemAsync(await SendAsync(HttpMethod.Put, $"/api/questions/{id}", token, JsonContent.Create(Essay())),
                       HttpStatusCode.NotFound, ErrorCodes.QuestionNotFound)) { }

        Assert.Equal(before, await SnapshotAsync(othersExam.Id));
    }

    // ---- Delete (D33, D34, S5) ----

    [Fact]
    public async Task Delete_Returns204_RemovesTheQuestionAndItsOptions_AndMovesTheLaterQuestionsUp()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id);
        var questions = new List<Question>();
        for (var position = 1; position <= 4; position++)
            questions.Add(await InsertQuestionAsync(exam.Id, position));
        var before = await SnapshotAsync(exam.Id);
        var deletedOptionIds = (await StoredQuestionsAsync(exam.Id))[1].Options.Select(o => o.Id).ToList();

        var response = await DeleteAsync(token, questions[1].Id);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(Ct));
        var stored = await StoredQuestionsAsync(exam.Id);
        Assert.Equal([questions[0].Id, questions[2].Id, questions[3].Id], stored.Select(q => q.Question.Id));
        Assert.Equal([1, 2, 3], stored.Select(q => q.Question.Position));
        Assert.All(stored, q => Assert.Equal(2, q.Options.Count));
        await using (var scope = factory.Services.CreateAsyncScope())
            Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().QuestionOptions.AnyAsync(o => deletedOptionIds.Contains(o.Id), Ct));

        // Apart from their positions, the remaining questions and their options are unchanged.
        var after = await SnapshotAsync(exam.Id);
        Assert.Equal(before[0], after[0]);
        Assert.Equal(before[2].Replace("|3|", "|2|"), after[1]);
        Assert.Equal(before[3].Replace("|4|", "|3|"), after[2]);

        using (await AssertProblemAsync(await DeleteAsync(token, questions[1].Id), HttpStatusCode.NotFound, ErrorCodes.QuestionNotFound)) { }

        // A new question goes after the last one again.
        using var created = await JsonAsync(await CreateAsync(token, exam.Id, Essay()));
        Assert.Equal(4, created.RootElement.GetProperty("position").GetInt32());
    }

    [Fact]
    public async Task Delete_TheLastQuestionOfADraft_Succeeds()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id);
        var question = await InsertQuestionAsync(exam.Id, 1);

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(token, question.Id)).StatusCode);

        Assert.Empty(await StoredQuestionsAsync(exam.Id));
    }

    [Fact]
    public async Task Delete_TheLastQuestionOfAPublishedExam_Returns409_ButAnyOtherQuestionCanGo()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id, Publish);
        var first = await InsertQuestionAsync(exam.Id, 1);
        var second = await InsertQuestionAsync(exam.Id, 2);

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(token, first.Id)).StatusCode);
        var before = await SnapshotAsync(exam.Id);

        using (var problem = await AssertProblemAsync(await DeleteAsync(token, second.Id), HttpStatusCode.Conflict, ErrorCodes.ExamNotEditable))
            Assert.Equal("A published exam must keep at least one question.", problem.RootElement.GetProperty("detail").GetString());

        Assert.Equal(before, await SnapshotAsync(exam.Id));
        Assert.Equal(1, Assert.Single(await StoredQuestionsAsync(exam.Id)).Question.Position);
    }

    [Fact]
    public async Task Delete_AnotherInstructorsQuestion_AnUnknownId_OrNotAnId_Returns404()
    {
        var (_, token) = await InstructorAsync();
        var otherInstructor = await _api.CreateUserAsync(RoleNames.Instructor);
        var othersExam = await InsertExamAsync(otherInstructor.Id);
        var othersQuestion = await InsertQuestionAsync(othersExam.Id, 1);
        var before = await SnapshotAsync(othersExam.Id);

        foreach (var id in new[] { othersQuestion.Id.ToString(), Guid.NewGuid().ToString(), "not-an-id" })
            using (await AssertProblemAsync(await SendAsync(HttpMethod.Delete, $"/api/questions/{id}", token), HttpStatusCode.NotFound, ErrorCodes.QuestionNotFound)) { }

        Assert.Equal(before, await SnapshotAsync(othersExam.Id));
    }

    // ---- The configured MCQ maximum (D32, D34, S5) ----

    [Fact]
    public async Task ALowerMaximum_ChangesNoStoredQuestion_AndOnlyTheQuestionBeingWrittenIsChecked()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id, Publish);
        var overLimit = await InsertQuestionAsync(exam.Id, 1, optionCount: 5);
        var other = await InsertQuestionAsync(exam.Id, 2);
        var third = await InsertQuestionAsync(exam.Id, 3);
        var overLimitBefore = (await SnapshotAsync(exam.Id))[0];

        await using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("Exams:MaxMcqOptions", "3"));
        var client = AuthorizedClient(host, token);

        // The stored over-limit question is unchanged and still listed.
        using (var list = JsonDocument.Parse(await client.GetStringAsync(QuestionsPath(exam.Id), Ct)))
            Assert.Equal(5, list.RootElement.GetProperty("items")[0].GetProperty("options").GetArrayLength());

        // Another question can be edited and deleted (S5).
        Assert.Equal(HttpStatusCode.OK,
            (await client.PutAsJsonAsync($"/api/questions/{other.Id}", Mcq(options: [("A", true), ("B", false), ("C", false)]), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/questions/{third.Id}", Ct)).StatusCode);

        // A new or edited question must meet the lower maximum.
        using (var problem = await AssertProblemAsync(
                   await client.PostAsJsonAsync(QuestionsPath(exam.Id), Mcq(options: [("A", true), ("B", false), ("C", false), ("D", false)]), Ct),
                   HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed))
            Assert.Equal("An MCQ question can have at most 3 options.", FieldError(problem, "options"));
        using (var problem = await AssertProblemAsync(
                   await client.PutAsJsonAsync($"/api/questions/{overLimit.Id}", Mcq(options: [.. Enumerable.Range(1, 5).Select(i => ($"Option {i}", i == 1))]), Ct),
                   HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed))
            Assert.Equal("An MCQ question can have at most 3 options.", FieldError(problem, "options"));
        Assert.Equal(overLimitBefore, (await SnapshotAsync(exam.Id))[0]);

        // Corrected by an edit.
        Assert.Equal(HttpStatusCode.OK,
            (await client.PutAsJsonAsync($"/api/questions/{overLimit.Id}", Mcq(options: [("A", true), ("B", false), ("C", false)]), Ct)).StatusCode);
        Assert.Equal([3, 3], (await StoredQuestionsAsync(exam.Id)).Select(q => q.Options.Count));
    }

    [Fact]
    public async Task AHigherMaximum_AllowsMoreOptions()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id);

        await using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("Exams:MaxMcqOptions", "12"));
        var client = AuthorizedClient(host, token);

        var response = await client.PostAsJsonAsync(QuestionsPath(exam.Id), Mcq(options: [.. Enumerable.Range(1, 12).Select(i => ($"Option {i}", i == 1))]), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(12, Assert.Single(await StoredQuestionsAsync(exam.Id)).Options.Count);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("-3")]
    public void AMaximumBelow2_PreventsStartup(string value)
    {
        using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("Exams:MaxMcqOptions", value));

        var exception = Record.Exception(() => host.Services);

        Assert.NotNull(exception);
        Assert.Contains("Exams:MaxMcqOptions must be at least 2.", AllMessages(exception));
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
        var question = await InsertQuestionAsync(exam.Id, 1);
        var before = await SnapshotAsync(exam.Id);
        var caller = await _api.CreateUserAsync(role);
        var session = await _api.LoginAsync(caller);

        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(endpoint, exam.Id, question.Id, session.AccessToken)).StatusCode);

        Assert.Equal(before, await SnapshotAsync(exam.Id));
    }

    [Theory]
    [MemberData(nameof(AllEndpoints))]
    public async Task WithoutAToken_Returns401TokenInvalid(string endpoint)
    {
        var (instructor, _) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id);
        var question = await InsertQuestionAsync(exam.Id, 1);
        var before = await SnapshotAsync(exam.Id);

        using var _ = await AssertProblemAsync(await CallAsync(endpoint, exam.Id, question.Id, accessToken: null),
            HttpStatusCode.Unauthorized, ErrorCodes.TokenInvalid);

        Assert.Equal(before, await SnapshotAsync(exam.Id));
    }

    [Theory]
    [MemberData(nameof(AllEndpoints))]
    public async Task AnInstructorWhoMustChangeTheirPassword_Gets403PasswordChangeRequired(string endpoint)
    {
        var instructor = await _api.CreateUserAsync(RoleNames.Instructor, mustChangePassword: true,
            temporaryPasswordExpiresAt: DateTimeOffset.UtcNow.AddHours(72));
        var session = await _api.LoginAsync(instructor);
        var exam = await InsertExamAsync(instructor.Id);
        var question = await InsertQuestionAsync(exam.Id, 1);
        var before = await SnapshotAsync(exam.Id);

        using var _ = await AssertProblemAsync(await CallAsync(endpoint, exam.Id, question.Id, session.AccessToken),
            HttpStatusCode.Forbidden, ErrorCodes.PasswordChangeRequired);

        Assert.Equal(before, await SnapshotAsync(exam.Id));
    }

    // ---- Writes to the same exam at the same moment (Phase 2 plan, R2) ----

    [Fact]
    public async Task CreateDuringACancellation_Returns409_AndAddsNothing()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id, Publish);
        await InsertQuestionAsync(exam.Id, 1);
        var before = await SnapshotAsync(exam.Id);

        // A cancellation has locked the exam and marked it Cancelled, but has not committed yet.
        await using var cancellation = await HeldTransaction.BeginAsync(factory);
        await cancellation.Db.Exams.Where(e => e.Id == exam.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, ExamStatus.Cancelled), Ct);

        var create = CreateAsync(token, exam.Id, Essay());
        await cancellation.WaitUntilItBlocksAnotherRequestAsync(factory);
        await cancellation.CommitAsync();

        using var _ = await AssertProblemAsync(await create, HttpStatusCode.Conflict, ErrorCodes.ExamNotEditable);
        Assert.Equal(before, await SnapshotAsync(exam.Id));
    }

    [Fact]
    public async Task CreateDuringAnotherCreate_WaitsForIt_AndTakesTheNextPosition()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id);

        // Another create has locked the exam and added question 1, but has not committed yet.
        await using var otherCreate = await HeldTransaction.BeginAsync(factory);
        await otherCreate.Db.Exams.Where(e => e.Id == exam.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.Title, e => e.Title), Ct);
        otherCreate.Db.Questions.Add(NewQuestion(exam.Id, 1, QuestionType.Essay));
        await otherCreate.Db.SaveChangesAsync(Ct);

        var create = CreateAsync(token, exam.Id, Essay(text: "Second"));
        await otherCreate.WaitUntilItBlocksAnotherRequestAsync(factory);
        await otherCreate.CommitAsync();

        var response = await create;
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = await JsonAsync(response);
        Assert.Equal(2, json.RootElement.GetProperty("position").GetInt32());
        Assert.Equal([1, 2], (await StoredQuestionsAsync(exam.Id)).Select(q => q.Question.Position));
    }

    [Fact]
    public async Task DeleteOfTheOnlyQuestion_DuringAPublication_Returns409_AndKeepsIt()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id);
        var question = await InsertQuestionAsync(exam.Id, 1);

        // A publication has locked the exam and marked it Published, but has not committed yet.
        await using var publication = await HeldTransaction.BeginAsync(factory);
        await publication.Db.Exams.Where(e => e.Id == exam.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, ExamStatus.Published).SetProperty(e => e.PublishedAt, DateTimeOffset.UtcNow), Ct);

        var delete = DeleteAsync(token, question.Id);
        await publication.WaitUntilItBlocksAnotherRequestAsync(factory);
        await publication.CommitAsync();

        using var _ = await AssertProblemAsync(await delete, HttpStatusCode.Conflict, ErrorCodes.ExamNotEditable);
        Assert.Equal(question.Id, Assert.Single(await StoredQuestionsAsync(exam.Id)).Question.Id);
    }

    [Fact]
    public async Task UpdateDuringADeleteOfTheSameQuestion_Returns404()
    {
        var (instructor, token) = await InstructorAsync();
        var exam = await InsertExamAsync(instructor.Id);
        var question = await InsertQuestionAsync(exam.Id, 1);

        // A delete has locked the exam and removed the question, but has not committed yet.
        await using var deletion = await HeldTransaction.BeginAsync(factory);
        await deletion.Db.Exams.Where(e => e.Id == exam.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.Title, e => e.Title), Ct);
        await deletion.Db.Questions.Where(q => q.Id == question.Id).ExecuteDeleteAsync(Ct);

        var update = UpdateAsync(token, question.Id, Essay(text: "Too late"));
        await deletion.WaitUntilItBlocksAnotherRequestAsync(factory);
        await deletion.CommitAsync();

        using var _ = await AssertProblemAsync(await update, HttpStatusCode.NotFound, ErrorCodes.QuestionNotFound);
        Assert.Empty(await StoredQuestionsAsync(exam.Id));
    }

    // ---- Helpers ----

    private sealed record StoredQuestion(Question Question, List<QuestionOption> Options);

    // Whole seconds a day from now, in UTC.
    private static DateTimeOffset InADay() =>
        DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).AddDays(1);

    private static string QuestionsPath(Guid examId) => $"/api/exams/{examId}/questions";

    private static object Mcq(
        string text = "Capital of France?", decimal marks = 2m, string? modelAnswer = "Paris",
        (string Text, bool IsCorrect)[]? options = null) =>
        new
        {
            type = "Mcq",
            text,
            marks,
            modelAnswer,
            options = (options ?? [("Paris", true), ("Lyon", false), ("Nice", false)]).Select(o => new { text = o.Text, isCorrect = o.IsCorrect }),
        };

    private static object Essay(string text = "Explain photosynthesis.", decimal marks = 10m, string? modelAnswer = null) =>
        new { type = "Essay", text, marks, modelAnswer };

    private static void Publish(Exam exam)
    {
        exam.Status = ExamStatus.Published;
        exam.PublishedAt = exam.StartsAt.AddDays(-7);
    }

    private async Task<(ApplicationUser User, string AccessToken)> InstructorAsync()
    {
        var user = await _api.CreateUserAsync(RoleNames.Instructor);
        return (user, (await _api.LoginAsync(user)).AccessToken);
    }

    private static HttpClient AuthorizedClient(WebApplicationFactory<Program> host, string accessToken)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? accessToken, HttpContent? content = null)
    {
        var client = factory.CreateClient();
        if (accessToken is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client.SendAsync(new HttpRequestMessage(method, path) { Content = content }, Ct);
    }

    private static StringContent RawJson(string json) => new(json, Encoding.UTF8, "application/json");

    private Task<HttpResponseMessage> ListAsync(string accessToken, string examId) =>
        SendAsync(HttpMethod.Get, $"/api/exams/{examId}/questions", accessToken);

    private Task<HttpResponseMessage> CreateAsync(string accessToken, Guid examId, object body) =>
        SendAsync(HttpMethod.Post, QuestionsPath(examId), accessToken, JsonContent.Create(body));

    private Task<HttpResponseMessage> UpdateAsync(string accessToken, Guid questionId, object body) =>
        SendAsync(HttpMethod.Put, $"/api/questions/{questionId}", accessToken, JsonContent.Create(body));

    private Task<HttpResponseMessage> DeleteAsync(string accessToken, Guid questionId) =>
        SendAsync(HttpMethod.Delete, $"/api/questions/{questionId}", accessToken);

    private Task<HttpResponseMessage> CallAsync(string endpoint, Guid examId, Guid questionId, string? accessToken) => endpoint switch
    {
        "list" => SendAsync(HttpMethod.Get, QuestionsPath(examId), accessToken),
        "create" => SendAsync(HttpMethod.Post, QuestionsPath(examId), accessToken, JsonContent.Create(Essay())),
        "update" => SendAsync(HttpMethod.Put, $"/api/questions/{questionId}", accessToken, JsonContent.Create(Essay(text: "Changed"))),
        "delete" => SendAsync(HttpMethod.Delete, $"/api/questions/{questionId}", accessToken),
        _ => throw new ArgumentOutOfRangeException(nameof(endpoint)),
    };

    private static async Task AssertNotEditableAsync(HttpResponseMessage response)
    {
        using var _ = await AssertProblemAsync(response, HttpStatusCode.Conflict, ErrorCodes.ExamNotEditable);
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

    private static string? FieldError(JsonDocument problem, string field) =>
        Assert.Single(problem.RootElement.GetProperty("errors").GetProperty(field).EnumerateArray()).GetString();

    private async Task<Exam> InsertExamAsync(Guid instructorId, Action<Exam>? change = null)
    {
        var exam = new Exam
        {
            Id = Guid.NewGuid(),
            InstructorId = instructorId,
            Title = "Question test exam",
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
        ModelAnswer = $"Answer {position}",
        Marks = 1.25m,
        Position = position,
    };

    // An MCQ (its first option correct) unless optionCount is 0 or the type is Essay.
    private async Task<Question> InsertQuestionAsync(
        Guid examId, int position, int optionCount = 2, QuestionType type = QuestionType.Mcq, bool reverseOptions = false)
    {
        var question = NewQuestion(examId, position, optionCount == 0 ? QuestionType.Essay : type);
        var options = Enumerable.Range(1, question.Type == QuestionType.Mcq ? optionCount : 0)
            .Select(i => new QuestionOption { Id = Guid.NewGuid(), QuestionId = question.Id, Text = $"Option {i}", IsCorrect = i == 1, Position = i })
            .ToList();
        if (reverseOptions)
            options.Reverse();

        await SaveAsync(db =>
        {
            db.Questions.Add(question);
            foreach (var option in options)
                db.QuestionOptions.Add(option);
        });
        return question;
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
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Exams.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, Ct);
    }

    // The exam's questions in position order, each with its options in position order.
    private async Task<List<StoredQuestion>> StoredQuestionsAsync(Guid examId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var questions = await db.Questions.AsNoTracking().Where(q => q.ExamId == examId).OrderBy(q => q.Position).ToListAsync(Ct);
        var questionIds = questions.Select(q => q.Id).ToList();
        var options = await db.QuestionOptions.AsNoTracking().Where(o => questionIds.Contains(o.QuestionId)).ToListAsync(Ct);
        return questions
            .Select(q => new StoredQuestion(q, options.Where(o => o.QuestionId == q.Id).OrderBy(o => o.Position).ToList()))
            .ToList();
    }

    // One line per question with every stored value, to check that nothing changed.
    private async Task<List<string>> SnapshotAsync(Guid examId) =>
        (await StoredQuestionsAsync(examId))
            .Select(s => string.Join('|',
                s.Question.Id, s.Question.Type, s.Question.Text, s.Question.Marks.ToString(CultureInfo.InvariantCulture),
                s.Question.ModelAnswer, s.Question.Position,
                string.Join(';', s.Options.Select(o => $"{o.Id}:{o.Text}:{o.IsCorrect}:{o.Position}"))))
            .ToList();

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
    // the exam (another question write, or a publication or cancellation from Step 2.4).
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
