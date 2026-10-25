using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ExamProctoring.Application.Common;
using ExamProctoring.Application.Exams.Import;
using ExamProctoring.Domain.Users;
using ExamProctoring.Infrastructure.Persistence;
using ExamProctoring.IntegrationTests.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static ExamProctoring.IntegrationTests.TestSupport.AuthApi;

namespace ExamProctoring.IntegrationTests;

// POST /api/exams/import/docx (owner decision 2026-10-24): a stateless read of an existing Word exam. It returns the
// questions for review, reports every problem, and never writes to the database.
[Collection(ApiCollection.Name)]
public sealed class ExamImportTests(ApiFactory factory)
{
    private const string Path = "/api/exams/import/docx";
    private const string DocxType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private readonly AuthApi _api = new(factory);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] SimpleExam() => new DocxBuilder().Paragraphs(
        "Midterm exam",
        "1. What is the capital of France?", "A) Paris", "B) Lyon", "C) Nice", "Answer: A", "Marks: 2",
        "2. Explain the water cycle.", "Marks: 5", "Model answer: Evaporation, condensation, precipitation.").Build();

    // ---- Successful reads

    [Fact]
    public async Task AValidDocument_Returns200_WithTheExactResponseShape()
    {
        var token = await InstructorTokenAsync();

        var response = await PostFileAsync(token, SimpleExam());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonAsync(response);
        var root = json.RootElement;
        Assert.Equal(["summary", "questions", "unassignedText", "unsupportedContent"], PropertyNames(root));
        Assert.Equal(["found", "ready", "needsAttention", "notImported"], PropertyNames(root.GetProperty("summary")));
        Assert.Equal(2, root.GetProperty("summary").GetProperty("found").GetInt32());
        Assert.Equal(2, root.GetProperty("summary").GetProperty("ready").GetInt32());
        Assert.Equal(["Midterm exam"], root.GetProperty("unassignedText").EnumerateArray().Select(e => e.GetString()));

        var mcq = root.GetProperty("questions")[0];
        Assert.Equal(["index", "status", "type", "text", "marks", "modelAnswer", "options", "issues", "sourceText"], PropertyNames(mcq));
        Assert.Equal(1, mcq.GetProperty("index").GetInt32());
        Assert.Equal("Ready", mcq.GetProperty("status").GetString());
        Assert.Equal("Mcq", mcq.GetProperty("type").GetString());
        Assert.Equal("What is the capital of France?", mcq.GetProperty("text").GetString());
        Assert.Equal(2m, mcq.GetProperty("marks").GetDecimal());
        Assert.Equal(JsonValueKind.Null, mcq.GetProperty("modelAnswer").ValueKind);
        Assert.Equal(["text", "isCorrect"], PropertyNames(mcq.GetProperty("options")[0]));
        Assert.Equal([true, false, false], mcq.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("isCorrect").GetBoolean()));
        Assert.Equal(0, mcq.GetProperty("issues").GetArrayLength());

        var essay = root.GetProperty("questions")[1];
        Assert.Equal("Essay", essay.GetProperty("type").GetString());
        Assert.Equal("Evaporation, condensation, precipitation.", essay.GetProperty("modelAnswer").GetString());
        Assert.Equal(0, essay.GetProperty("options").GetArrayLength());
    }

    [Fact]
    public async Task AReadyQuestion_CanBeSavedUnchanged_WithTheExistingQuestionEndpoint()
    {
        var token = await InstructorTokenAsync();
        using var json = await JsonAsync(await PostFileAsync(token, SimpleExam()));
        var client = Authorized(token);
        var exam = await client.PostAsJsonAsync("/api/exams", new
        {
            title = "Imported",
            startsAt = DateTimeOffset.UtcNow.AddDays(3).ToString("O"),
            durationMinutes = 60,
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, exam.StatusCode);
        using var examJson = await JsonAsync(exam);
        var examId = examJson.RootElement.GetProperty("id").GetString();

        foreach (var question in json.RootElement.GetProperty("questions").EnumerateArray())
        {
            var saved = await client.PostAsync($"/api/exams/{examId}/questions",
                new StringContent(question.GetRawText(), Encoding.UTF8, "application/json"), Ct);
            Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        }
    }

    [Fact]
    public async Task AnArabicDocument_IsRead()
    {
        var token = await InstructorTokenAsync();
        var docx = new DocxBuilder().Paragraphs("س1: ما عاصمة فرنسا؟", "أ) باريس", "ب) ليون", "الإجابة: أ", "الدرجة: ٢").Build();

        using var json = await JsonAsync(await PostFileAsync(token, docx));

        var question = json.RootElement.GetProperty("questions")[0];
        Assert.Equal("Ready", question.GetProperty("status").GetString());
        Assert.Equal("ما عاصمة فرنسا؟", question.GetProperty("text").GetString());
        Assert.Equal("باريس", question.GetProperty("options")[0].GetProperty("text").GetString());
        Assert.True(question.GetProperty("options")[0].GetProperty("isCorrect").GetBoolean());
    }

    [Fact]
    public async Task WordAutomaticNumbering_GivesTheQuestionNumbersAndOptionLetters()
    {
        var token = await InstructorTokenAsync();
        var docx = new DocxBuilder()
            .List(1, NumberFormatValues.Decimal, "%1.", 1)
            .List(2, NumberFormatValues.UpperLetter, "%1)", 2, 3)
            .Numbered(1, "First question").Numbered(2, "Paris").Numbered(2, "Lyon").Paragraphs("Answer: B", "Marks: 1")
            .Numbered(1, "Second question").Numbered(3, "Yes").Numbered(3, "No").Paragraphs("Answer: A", "Marks: 1")
            .Build();

        using var json = await JsonAsync(await PostFileAsync(token, docx));

        var questions = json.RootElement.GetProperty("questions");
        Assert.Equal(2, questions.GetArrayLength());
        Assert.All(questions.EnumerateArray(), q => Assert.Equal("Ready", q.GetProperty("status").GetString()));
        Assert.Equal("1. First question\nA) Paris\nB) Lyon\nAnswer: B\nMarks: 1", questions[0].GetProperty("sourceText").GetString());
        Assert.Equal("2. Second question", questions[1].GetProperty("sourceText").GetString()!.Split('\n')[0]);
        Assert.Equal("A) Yes", questions[1].GetProperty("sourceText").GetString()!.Split('\n')[1]);
    }

    [Fact]
    public async Task ArabicAutomaticNumbering_UsesArabicLetters()
    {
        var token = await InstructorTokenAsync();
        var docx = new DocxBuilder()
            .List(1, NumberFormatValues.ArabicAbjad, "%1)", 1)
            .Paragraph("س1: سؤال").Numbered(1, "نعم").Numbered(1, "لا").Paragraphs("الإجابة: ب", "الدرجة: 1")
            .Build();

        using var json = await JsonAsync(await PostFileAsync(token, docx));

        var question = json.RootElement.GetProperty("questions")[0];
        Assert.Equal("Ready", question.GetProperty("status").GetString());
        Assert.Equal([false, true], question.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("isCorrect").GetBoolean()));
    }

    [Fact]
    public async Task LineBreaksInsideAParagraph_SeparateTheLines()
    {
        var token = await InstructorTokenAsync();
        var docx = new DocxBuilder().Paragraph("1. Q\n*A) x\nB) y\nMarks: 1").Build();

        using var json = await JsonAsync(await PostFileAsync(token, docx));

        Assert.Equal("Ready", json.RootElement.GetProperty("questions")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task ADocumentWithoutRecognizableQuestions_Returns200_WithFoundZero()
    {
        var token = await InstructorTokenAsync();

        using var json = await JsonAsync(await PostFileAsync(token, new DocxBuilder().Paragraphs("Notes", "Nothing numbered here").Build()));

        Assert.Equal(0, json.RootElement.GetProperty("summary").GetProperty("found").GetInt32());
        Assert.Equal(2, json.RootElement.GetProperty("unassignedText").GetArrayLength());
    }

    [Fact]
    public async Task AnEmptyDocument_Returns200_WithFoundZero()
    {
        var token = await InstructorTokenAsync();

        using var json = await JsonAsync(await PostFileAsync(token, new DocxBuilder().Build()));

        Assert.Equal(0, json.RootElement.GetProperty("summary").GetProperty("found").GetInt32());
    }

    [Fact]
    public async Task MissingAnswerAndMarks_AreReported_NotInvented()
    {
        var token = await InstructorTokenAsync();
        var docx = new DocxBuilder().Paragraphs("1. Q", "A) x", "B) y").Build();

        using var json = await JsonAsync(await PostFileAsync(token, docx));

        var question = json.RootElement.GetProperty("questions")[0];
        Assert.Equal("NeedsAttention", question.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, question.GetProperty("marks").ValueKind);
        Assert.All(question.GetProperty("options").EnumerateArray(), o => Assert.False(o.GetProperty("isCorrect").GetBoolean()));
        var codes = question.GetProperty("issues").EnumerateArray().Select(i => i.GetProperty("code").GetString()).ToList();
        Assert.Contains("no_correct_answer", codes);
        Assert.Contains("marks_missing", codes);
    }

    // ---- Tables and unsupported content

    [Fact]
    public async Task ASimpleTable_IsReadRowByRow()
    {
        var token = await InstructorTokenAsync();
        var docx = new DocxBuilder().Table(
            ["1. What is the capital of France?"],
            ["A)", "Paris"],
            ["*B)", "Lyon"],
            ["Marks: 2"]).Build();

        using var json = await JsonAsync(await PostFileAsync(token, docx));

        var root = json.RootElement;
        Assert.Equal(0, root.GetProperty("unsupportedContent").GetArrayLength());
        var question = root.GetProperty("questions")[0];
        Assert.Equal("Ready", question.GetProperty("status").GetString());
        Assert.Equal(["Paris", "Lyon"], question.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("text").GetString()));
        Assert.Equal([false, true], question.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("isCorrect").GetBoolean()));
    }

    [Theory]
    [InlineData("three-cells")]
    [InlineData("two-text-cells")]
    [InlineData("merged-cells")]
    public async Task AnAmbiguousTable_IsReportedAsUnsupported_AndItsTextKept(string kind)
    {
        var token = await InstructorTokenAsync();
        var builder = new DocxBuilder().Paragraphs("1. Which city is the capital?");
        builder = kind switch
        {
            "three-cells" => builder.Table(["A) Paris", "B) Lyon", "C) Nice"]),
            "two-text-cells" => builder.Table(["Paris is a city", "Lyon is a city"]),
            _ => builder.MergedCellTable("Paris", "Lyon"),
        };

        using var json = await JsonAsync(await PostFileAsync(token, builder.Paragraphs("Marks: 1").Build()));

        var root = json.RootElement;
        var unsupported = Assert.Single(root.GetProperty("unsupportedContent").EnumerateArray());
        Assert.Equal("Table", unsupported.GetProperty("kind").GetString());
        Assert.Equal(1, unsupported.GetProperty("questionIndex").GetInt32());
        Assert.NotEqual(0, root.GetProperty("unassignedText").GetArrayLength());
        var question = root.GetProperty("questions")[0];
        Assert.Equal("NeedsAttention", question.GetProperty("status").GetString());
        Assert.Equal(0, question.GetProperty("options").GetArrayLength());
    }

    [Fact]
    public async Task ImagesAndEquations_AreReported_PerQuestion()
    {
        var token = await InstructorTokenAsync();
        var docx = new DocxBuilder()
            .ParagraphWithImage("1. Look at the picture")
            .Paragraphs("*A) x", "B) y", "Marks: 1")
            .ParagraphWithEquation("2. Solve")
            .Paragraphs("Marks: 3")
            .Build();

        using var json = await JsonAsync(await PostFileAsync(token, docx));

        var root = json.RootElement;
        Assert.Equal(
            [(1, "Image"), (2, "Equation")],
            root.GetProperty("unsupportedContent").EnumerateArray()
                .Select(u => (u.GetProperty("questionIndex").GetInt32(), u.GetProperty("kind").GetString())));
        Assert.Equal("Look at the picture", root.GetProperty("questions")[0].GetProperty("text").GetString());
        Assert.All(root.GetProperty("questions").EnumerateArray(), q => Assert.Contains(
            q.GetProperty("issues").EnumerateArray(), i => i.GetProperty("code").GetString() == "unsupported_content"));
    }

    // ---- Files that are refused

    [Theory]
    [InlineData("exam.doc")]
    [InlineData("exam.pdf")]
    [InlineData("exam.txt")]
    public async Task AnotherExtension_Returns400_ImportFileUnsupported(string fileName)
    {
        var token = await InstructorTokenAsync();

        var response = await PostFileAsync(token, SimpleExam(), fileName);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ImportFileUnsupported);
    }

    [Fact]
    public async Task APdfRenamedToDocx_Returns400_ImportFileUnsupported()
    {
        var token = await InstructorTokenAsync();

        var response = await PostFileAsync(token, Encoding.ASCII.GetBytes("%PDF-1.7\n% not a word document"));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ImportFileUnsupported);
    }

    [Fact]
    public async Task ATextFileRenamedToDocx_Returns400_ImportFileUnsupported()
    {
        var token = await InstructorTokenAsync();

        var response = await PostFileAsync(token, Encoding.UTF8.GetBytes("1. Q\nA) x\nB) y"));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ImportFileUnsupported);
    }

    [Theory]
    [InlineData(WordprocessingDocumentType.MacroEnabledDocument)]
    [InlineData(WordprocessingDocumentType.Template)]
    public async Task AMacroEnabledDocumentOrTemplate_Returns400_ImportFileUnsupported(WordprocessingDocumentType type)
    {
        var token = await InstructorTokenAsync();

        var response = await PostFileAsync(token, new DocxBuilder().Paragraphs("1. Q").Build(type));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ImportFileUnsupported);
    }

    [Fact]
    public async Task APasswordProtectedDocument_Returns400_ImportFileUnreadable()
    {
        // Encrypted Word documents are OLE compound files, not ZIP packages.
        var token = await InstructorTokenAsync();
        byte[] ole = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, .. new byte[512]];

        var response = await PostFileAsync(token, ole);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ImportFileUnreadable);
    }

    [Fact]
    public async Task ACorruptedDocument_Returns400_ImportFileUnreadable()
    {
        var token = await InstructorTokenAsync();
        var docx = SimpleExam();

        var response = await PostFileAsync(token, docx[..(docx.Length / 2)]);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ImportFileUnreadable);
    }

    [Fact]
    public async Task AZipWithoutAWordDocument_Returns400_ImportFileUnreadable()
    {
        var token = await InstructorTokenAsync();
        using var stream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            await using var entry = zip.CreateEntry("notes.txt").Open();
            await entry.WriteAsync("hello"u8.ToArray(), Ct);
        }

        var response = await PostFileAsync(token, stream.ToArray());

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ImportFileUnreadable);
    }

    [Fact]
    public async Task AZipBomb_Returns400_ImportFileUnreadable()
    {
        // About 60 MB of zeros once unpacked, a few hundred KB uploaded.
        var token = await InstructorTokenAsync();
        var bomb = DocxBuilder.WithExtraEntries(SimpleExam(), count: 1, bytesPerEntry: 60L * 1024 * 1024);
        Assert.True(bomb.Length < ExamImportService.MaxFileBytes);

        var response = await PostFileAsync(token, bomb);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ImportFileUnreadable);
    }

    [Fact]
    public async Task TooManyParts_Returns400_ImportFileUnreadable()
    {
        var token = await InstructorTokenAsync();
        var docx = DocxBuilder.WithExtraEntries(SimpleExam(), count: 1001, bytesPerEntry: 1);

        var response = await PostFileAsync(token, docx);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ImportFileUnreadable);
    }

    // ---- Request problems

    [Fact]
    public async Task NoFile_Returns400_ValidationFailed_OnTheFileField()
    {
        var token = await InstructorTokenAsync();
        using var form = new MultipartFormDataContent { { new StringContent("x"), "note" } };

        var response = await Authorized(token).PostAsync(Path, form, Ct);

        using var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
        Assert.Equal("A .docx file is required.", problem.RootElement.GetProperty("errors").GetProperty("file")[0].GetString());
    }

    [Fact]
    public async Task ANonMultipartBody_Returns400_ValidationFailed()
    {
        var token = await InstructorTokenAsync();

        var response = await Authorized(token).PostAsJsonAsync(Path, new { file = "x" }, Ct);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task TwoFiles_Return400_ValidationFailed()
    {
        var token = await InstructorTokenAsync();
        using var form = new MultipartFormDataContent { { FileContent(SimpleExam()), "file", "a.docx" }, { FileContent(SimpleExam()), "other", "b.docx" } };

        var response = await Authorized(token).PostAsync(Path, form, Ct);

        using var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
        Assert.Equal("Upload exactly one file.", problem.RootElement.GetProperty("errors").GetProperty("file")[0].GetString());
    }

    [Fact]
    public async Task AnEmptyFile_Returns400_ValidationFailed()
    {
        var token = await InstructorTokenAsync();

        var response = await PostFileAsync(token, []);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task AFileOver10MB_Returns413()
    {
        var token = await InstructorTokenAsync();

        var response = await PostFileAsync(token, new byte[ExamImportService.MaxFileBytes + 1]);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    // The in-memory test server does not enforce body limits, so this test runs the API on real Kestrel.
    [Fact]
    public async Task OnKestrel_ABodyOverTheLimit_Returns413ProblemDetails()
    {
        await using var kestrel = new KestrelApiFactory();
        kestrel.UseKestrel(0);
        kestrel.StartServer();
        var kestrelApi = new AuthApi(kestrel);
        var user = await kestrelApi.CreateUserAsync(RoleNames.Instructor);
        var client = kestrel.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await kestrelApi.LoginAsync(user)).AccessToken);

        // "Expect: 100-continue" makes the client wait for the server's answer, so Kestrel can refuse the body before
        // it is sent instead of closing the connection mid-upload.
        client.DefaultRequestHeaders.ExpectContinue = true;

        using var form = new MultipartFormDataContent { { FileContent(new byte[ExamImportService.MaxRequestBytes + 1]), "file", "big.docx" } };
        var response = await client.PostAsync(Path, form, Ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    // ---- Access

    [Fact]
    public async Task WithoutAToken_Returns401_TokenInvalid()
    {
        var response = await factory.CreateClient().PostAsync(Path, FileForm(SimpleExam()), Ct);

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, ErrorCodes.TokenInvalid);
    }

    [Theory]
    [InlineData(RoleNames.Student)]
    [InlineData(RoleNames.Admin)]
    public async Task OtherRoles_Get403(string role)
    {
        var user = await _api.CreateUserAsync(role);
        var token = (await _api.LoginAsync(user)).AccessToken;

        var response = await Authorized(token).PostAsync(Path, FileForm(SimpleExam()), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task APendingPasswordChange_Returns403_PasswordChangeRequired()
    {
        var user = await _api.CreateUserAsync(RoleNames.Instructor, mustChangePassword: true,
            temporaryPasswordExpiresAt: DateTimeOffset.UtcNow.AddDays(1));
        var token = (await _api.LoginAsync(user)).AccessToken;

        var response = await Authorized(token).PostAsync(Path, FileForm(SimpleExam()), Ct);

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, ErrorCodes.PasswordChangeRequired);
    }

    [Fact]
    public async Task MoreThan10RequestsAMinute_FromOneAddress_Return429()
    {
        var token = await InstructorTokenAsync();
        var client = Authorized(token);
        client.DefaultRequestHeaders.Add(ApiFactory.ClientIpHeader, "203.0.113.77");

        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(Path, FileForm(SimpleExam()), Ct)).StatusCode);

        var limited = await client.PostAsync(Path, FileForm(SimpleExam()), Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);

        // Another address is not affected.
        var other = Authorized(token);
        other.DefaultRequestHeaders.Add(ApiFactory.ClientIpHeader, "203.0.113.78");
        Assert.Equal(HttpStatusCode.OK, (await other.PostAsync(Path, FileForm(SimpleExam()), Ct)).StatusCode);
    }

    // ---- Question splitting (audit 2026-10-25)

    [Fact]
    public async Task NumberedModelAnswersAndDecimalLines_DoNotSplitQuestions()
    {
        var token = await InstructorTokenAsync();
        var docx = new DocxBuilder().Paragraphs(
            "1. Explain the water cycle.", "Marks: 5", "Model answer:",
            "1. Evaporation", "2. Condensation", "3. Precipitation",
            "2. A box weighs", "2.5 kg. What is its mass in grams?", "Marks: 1").Build();

        using var json = await JsonAsync(await PostFileAsync(token, docx));

        var questions = json.RootElement.GetProperty("questions");
        Assert.Equal(2, questions.GetArrayLength());
        Assert.All(questions.EnumerateArray(), q => Assert.Equal("Ready", q.GetProperty("status").GetString()));
        Assert.Equal("Explain the water cycle.", questions[0].GetProperty("text").GetString());
        Assert.Equal("1. Evaporation\n2. Condensation\n3. Precipitation", questions[0].GetProperty("modelAnswer").GetString());
        Assert.Equal("A box weighs\n2.5 kg. What is its mass in grams?", questions[1].GetProperty("text").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("unassignedText").GetArrayLength());
    }

    // Final review probe B, through a Word file: three valid questions must not disappear into one Ready question.
    [Fact]
    public async Task QuestionsWithInlineMarks_AfterANumberedModelAnswer_AreAllReturned()
    {
        var token = await InstructorTokenAsync();
        var docx = new DocxBuilder().Paragraphs(
            "1. Explain the water cycle.", "Marks: 5", "Model answer:", "1. Evaporation",
            "2. Define condensation. (2 marks)", "3. Define precipitation. (3 marks)", "4. Name one greenhouse gas. (1 mark)").Build();

        using var json = await JsonAsync(await PostFileAsync(token, docx));

        var root = json.RootElement;
        Assert.Equal(4, root.GetProperty("summary").GetProperty("found").GetInt32());
        Assert.Equal(0, root.GetProperty("summary").GetProperty("ready").GetInt32());
        var questions = root.GetProperty("questions");
        Assert.Equal("1. Evaporation", questions[0].GetProperty("modelAnswer").GetString());
        Assert.Equal(["Explain the water cycle.", "Define condensation.", "Define precipitation.", "Name one greenhouse gas."],
            questions.EnumerateArray().Select(q => q.GetProperty("text").GetString()));
        Assert.All(questions.EnumerateArray(), q => Assert.Contains(
            q.GetProperty("issues").EnumerateArray(), i => i.GetProperty("code").GetString() == "ambiguous_split"));
    }

    // ---- Word numbering bounds (final review)

    [Theory]
    [InlineData("arabicAbjad")]
    [InlineData("arabicAlpha")]
    [InlineData("upperLetter")]
    [InlineData("decimal")]
    public async Task AListStartingBelowZero_Returns400_ImportFileUnreadable(string format)
    {
        var token = await InstructorTokenAsync();
        var docx = new DocxBuilder().ListStartingAt(1, new NumberFormatValues(format), "%1)", -100, 1).Numbered(1, "Option").Build();

        var response = await PostFileAsync(token, docx);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ImportFileUnreadable);
    }

    [Theory]
    [InlineData(999, "KKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKK)")]
    [InlineData(1000, "1000)")]
    [InlineData(260_000_000, "260000000)")]
    [InlineData(int.MaxValue, "2147483647)")]
    public async Task AVeryLargeListStart_IsWrittenAsDigits_AndTheResponseStaysSmall(int start, string label)
    {
        // Before the bound, 260,000,000 gave a 10,000,016-character letter label from a file of a few KB.
        var token = await InstructorTokenAsync();
        var docx = new DocxBuilder()
            .ListStartingAt(1, NumberFormatValues.UpperLetter, "%1)", start, 1)
            .Numbered(1, "First").Numbered(1, "Second")
            .Build();

        var response = await PostFileAsync(token, docx);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(body.Length < 2_000, $"The response has {body.Length} characters.");
        using var json = JsonDocument.Parse(body);
        Assert.Equal(label + " First", json.RootElement.GetProperty("unassignedText")[0].GetString());
    }

    // ---- Privacy

    [Fact]
    public async Task DocumentContent_IsNeverWrittenToTheLogs()
    {
        // Every log entry of the API is captured, at every level, while documents carrying unique markers are imported
        // and refused. The markers sit in question text, options, a model answer, unassigned text, a table and the
        // file name.
        const string marker = "ZQX7781PRIVATE";
        const string arabicMarker = "سري٧٧٨١خاص";
        var capture = new CapturingLoggerProvider();
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureLogging(logging =>
        {
            logging.AddProvider(capture);
            logging.AddFilter<CapturingLoggerProvider>(null, LogLevel.Trace);
        }));
        var token = await InstructorTokenAsync();
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var document = new DocxBuilder()
            .Paragraphs($"Exam notes {marker}",
                $"1. Question {marker} {arabicMarker}", $"A) Option {marker}", "B) Other", "Answer: A", "Marks: 2",
                $"2. Essay {marker}", "Marks: 4", $"Model answer: {marker}", $"Stray line {marker}")
            .Table([$"cell {marker}", "second", "third"])
            .Build();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(Path, FileForm(document, $"{marker}.docx"), Ct)).StatusCode);

        var macro = new DocxBuilder().Paragraphs($"1. {marker}").Build(WordprocessingDocumentType.MacroEnabledDocument);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(Path, FileForm(macro, $"{marker}.docx"), Ct)).StatusCode);
        var text = Encoding.UTF8.GetBytes($"1. {marker} {arabicMarker}");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(Path, FileForm(text, $"{marker}.docx"), Ct)).StatusCode);
        var corrupt = document[..(document.Length / 2)];
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(Path, FileForm(corrupt, $"{marker}.docx"), Ct)).StatusCode);

        var entries = capture.Entries.ToList();
        Assert.Contains(entries, entry => entry.Contains("Word import read 2 questions", StringComparison.Ordinal));
        Assert.Contains(entries, entry => entry.Contains("Word import refused a file", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, entry => entry.Contains(marker, StringComparison.Ordinal));
        Assert.DoesNotContain(entries, entry => entry.Contains(arabicMarker, StringComparison.Ordinal));
    }

    // ---- Stateless

    [Fact]
    public async Task Importing_WritesNothingToTheDatabase()
    {
        var token = await InstructorTokenAsync();
        var before = await CountRowsAsync();

        Assert.Equal(HttpStatusCode.OK, (await PostFileAsync(token, SimpleExam())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostFileAsync(token, [1, 2, 3])).StatusCode);

        Assert.Equal(before, await CountRowsAsync());
    }

    // ---- Helpers

    private async Task<string> InstructorTokenAsync()
    {
        var user = await _api.CreateUserAsync(RoleNames.Instructor);
        return (await _api.LoginAsync(user)).AccessToken;
    }

    private HttpClient Authorized(string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private Task<HttpResponseMessage> PostFileAsync(string token, byte[] bytes, string fileName = "exam.docx") =>
        Authorized(token).PostAsync(Path, FileForm(bytes, fileName), Ct);

    private static MultipartFormDataContent FileForm(byte[] bytes, string fileName = "exam.docx") =>
        new() { { FileContent(bytes), "file", fileName } };

    private static ByteArrayContent FileContent(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(DocxType);
        return content;
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode is HttpStatusCode.Created ? HttpStatusCode.OK : response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
    }

    private static string[] PropertyNames(JsonElement element) => element.EnumerateObject().Select(p => p.Name).ToArray();

    private async Task<(int Exams, int Questions, int Options, int Users, int Tokens)> CountRowsAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.Exams.CountAsync(Ct), await db.Questions.CountAsync(Ct), await db.QuestionOptions.CountAsync(Ct),
            await db.Users.CountAsync(Ct), await db.RefreshTokens.CountAsync(Ct));
    }
}
