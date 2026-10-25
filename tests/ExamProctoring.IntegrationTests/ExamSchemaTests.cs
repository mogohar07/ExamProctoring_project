using ExamProctoring.Domain.Exams;
using ExamProctoring.Domain.Users;
using ExamProctoring.Infrastructure.Persistence;
using ExamProctoring.IntegrationTests.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ExamProctoring.IntegrationTests;

// Phase 2 Step 2.1: the Exams, Questions and QuestionOptions tables created by the AddExams migration (§5.1-§5.3),
// checked against SQL Server's own catalog and by writing real rows to the shared test database.
[Collection(ApiCollection.Name)]
public sealed class ExamSchemaTests(ApiFactory factory)
{
    private const string ExamTables = "N'Exams', N'Questions', N'QuestionOptions'";

    // SQL Server error numbers.
    private const int ConstraintViolation = 547;
    private const int UniqueIndexViolation = 2601;
    private const int StringTruncated = 2628;

    private readonly AuthApi _api = new(factory);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Exact schema ----

    [Fact]
    public async Task Columns_HaveTheApprovedTypesLengthsAndNullability()
    {
        var actual = await QueryAsync<ColumnRow>($"""
            SELECT t.name AS TableName, c.name AS ColumnName, ty.name AS TypeName, CAST(c.max_length AS int) AS MaxLength,
                   CAST(c.precision AS int) AS [Precision], CAST(c.scale AS int) AS Scale, c.is_nullable AS IsNullable
            FROM sys.columns c
            JOIN sys.tables t ON t.object_id = c.object_id
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE t.name IN ({ExamTables})
            """);

        ColumnRow[] expected =
        [
            GuidColumn("Exams", "Id"),
            GuidColumn("Exams", "InstructorId"),
            NVarCharColumn("Exams", "Title", ExamFieldLimits.TitleMaxLength, nullable: false),
            NVarCharColumn("Exams", "Description", ExamFieldLimits.DescriptionMaxLength, nullable: true),
            DateTimeOffsetColumn("Exams", "StartsAt", nullable: false),
            IntColumn("Exams", "DurationMinutes"),
            NVarCharColumn("Exams", "Status", 30, nullable: false),
            IntColumn("Exams", "WarningLimit"),
            DateTimeOffsetColumn("Exams", "PublishedAt", nullable: true),
            DateTimeOffsetColumn("Exams", "ResultsPublishedAt", nullable: true),
            DateTimeOffsetColumn("Exams", "AnalysisCompletedAt", nullable: true),
            new() { TableName = "Exams", ColumnName = "RowVersion", TypeName = "timestamp", MaxLength = 8, Precision = 0, Scale = 0, IsNullable = false },

            GuidColumn("Questions", "Id"),
            GuidColumn("Questions", "ExamId"),
            NVarCharColumn("Questions", "Type", 30, nullable: false),
            NVarCharColumn("Questions", "Text", ExamFieldLimits.QuestionTextMaxLength, nullable: false),
            NVarCharColumn("Questions", "ModelAnswer", ExamFieldLimits.ModelAnswerMaxLength, nullable: true),
            new() { TableName = "Questions", ColumnName = "Marks", TypeName = "decimal", MaxLength = 5, Precision = 6, Scale = 2, IsNullable = true },
            IntColumn("Questions", "Position"),

            GuidColumn("QuestionOptions", "Id"),
            GuidColumn("QuestionOptions", "QuestionId"),
            NVarCharColumn("QuestionOptions", "Text", ExamFieldLimits.OptionTextMaxLength, nullable: false),
            new() { TableName = "QuestionOptions", ColumnName = "IsCorrect", TypeName = "bit", MaxLength = 1, Precision = 1, Scale = 0, IsNullable = false },
            IntColumn("QuestionOptions", "Position"),
        ];

        Assert.Equal(Sorted(expected), Sorted(actual));
    }

    [Fact]
    public async Task ForeignKeys_HaveTheApprovedTargetsAndDeleteActions()
    {
        var actual = await QueryAsync<ForeignKeyRow>($"""
            SELECT fk.name AS Name, OBJECT_NAME(fk.parent_object_id) AS TableName, OBJECT_NAME(fk.referenced_object_id) AS ReferencedTable,
                   fk.delete_referential_action_desc AS DeleteAction,
                   (SELECT STRING_AGG(c.name, ',') FROM sys.foreign_key_columns fkc
                    JOIN sys.columns c ON c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id
                    WHERE fkc.constraint_object_id = fk.object_id) AS Columns
            FROM sys.foreign_keys fk
            WHERE OBJECT_NAME(fk.parent_object_id) IN ({ExamTables}) OR OBJECT_NAME(fk.referenced_object_id) IN ({ExamTables})
            """);

        ForeignKeyRow[] expected =
        [
            new() { Name = "FK_Exams_AspNetUsers_InstructorId", TableName = "Exams", ReferencedTable = "AspNetUsers", DeleteAction = "NO_ACTION", Columns = "InstructorId" },
            new() { Name = "FK_Questions_Exams_ExamId", TableName = "Questions", ReferencedTable = "Exams", DeleteAction = "CASCADE", Columns = "ExamId" },
            new() { Name = "FK_QuestionOptions_Questions_QuestionId", TableName = "QuestionOptions", ReferencedTable = "Questions", DeleteAction = "CASCADE", Columns = "QuestionId" },
        ];

        Assert.Equal(expected.OrderBy(r => r.Name), actual.OrderBy(r => r.Name));
    }

    [Fact]
    public async Task Indexes_AreExactlyThePrimaryKeysAndTheApprovedIndexes()
    {
        var actual = await QueryAsync<IndexRow>($"""
            SELECT i.name AS Name, OBJECT_NAME(i.object_id) AS TableName, i.is_unique AS IsUnique, i.is_primary_key AS IsPrimaryKey,
                   (SELECT STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal) FROM sys.index_columns ic
                    JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                    WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0) AS Columns
            FROM sys.indexes i
            WHERE i.type > 0 AND OBJECT_NAME(i.object_id) IN ({ExamTables})
            """);

        IndexRow[] expected =
        [
            new() { Name = "PK_Exams", TableName = "Exams", IsUnique = true, IsPrimaryKey = true, Columns = "Id" },
            new() { Name = "IX_Exams_InstructorId_StartsAt", TableName = "Exams", IsUnique = false, IsPrimaryKey = false, Columns = "InstructorId,StartsAt" },
            new() { Name = "PK_Questions", TableName = "Questions", IsUnique = true, IsPrimaryKey = true, Columns = "Id" },
            new() { Name = "IX_Questions_ExamId_Position", TableName = "Questions", IsUnique = true, IsPrimaryKey = false, Columns = "ExamId,Position" },
            new() { Name = "PK_QuestionOptions", TableName = "QuestionOptions", IsUnique = true, IsPrimaryKey = true, Columns = "Id" },
            new() { Name = "IX_QuestionOptions_QuestionId_Position", TableName = "QuestionOptions", IsUnique = true, IsPrimaryKey = false, Columns = "QuestionId,Position" },
        ];

        Assert.Equal(expected.OrderBy(r => r.Name), actual.OrderBy(r => r.Name));
    }

    [Fact]
    public async Task CheckConstraints_AreExactlyTheApprovedOnes_AndThereAreNoDefaults()
    {
        var checks = await QueryAsync<CheckRow>($"""
            SELECT cc.name AS Name, OBJECT_NAME(cc.parent_object_id) AS TableName, cc.definition AS Definition
            FROM sys.check_constraints cc
            WHERE OBJECT_NAME(cc.parent_object_id) IN ({ExamTables})
            """);
        var defaults = await QueryAsync<CheckRow>($"""
            SELECT dc.name AS Name, OBJECT_NAME(dc.parent_object_id) AS TableName, dc.definition AS Definition
            FROM sys.default_constraints dc
            WHERE OBJECT_NAME(dc.parent_object_id) IN ({ExamTables})
            """);

        CheckRow[] expected =
        [
            new() { Name = "CK_Exams_DurationMinutes", TableName = "Exams", Definition = "([DurationMinutes]>(0))" },
            new() { Name = "CK_Exams_WarningLimit", TableName = "Exams", Definition = "([WarningLimit]>(0))" },
            new() { Name = "CK_Questions_Marks", TableName = "Questions", Definition = "([Marks]>(0))" },
        ];

        Assert.Equal(expected.OrderBy(r => r.Name), checks.OrderBy(r => r.Name));
        Assert.Empty(defaults);
    }

    // ---- Rows the database rejects or accepts ----

    [Theory]
    [InlineData(0, 3)]
    [InlineData(-1, 3)]
    [InlineData(60, 0)]
    [InlineData(60, -1)]
    public async Task ExamWithoutAPositiveDurationOrWarningLimit_IsRejected(int durationMinutes, int warningLimit)
    {
        var instructor = await _api.CreateUserAsync(RoleNames.Instructor);

        await AssertSaveFailsAsync(ConstraintViolation, db =>
            db.Exams.Add(NewExam(instructor.Id, e =>
            {
                e.DurationMinutes = durationMinutes;
                e.WarningLimit = warningLimit;
            })));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.01")]
    public async Task QuestionWithoutPositiveMarks_IsRejected(string marks)
    {
        var exam = await InsertExamAsync();

        await AssertSaveFailsAsync(ConstraintViolation, db =>
            db.Questions.Add(NewQuestion(exam.Id, 1, q => q.Marks = decimal.Parse(marks, System.Globalization.CultureInfo.InvariantCulture))));
    }

    [Fact]
    public async Task SmallestAndLargestMarks_AreAccepted()
    {
        var exam = await InsertExamAsync();

        await SaveAsync(db => db.Questions.AddRange(
            NewQuestion(exam.Id, 1, q => q.Marks = 0.01m),
            NewQuestion(exam.Id, 2, q => q.Marks = 9999.99m)));

        await using var scope = factory.Services.CreateAsyncScope();
        var marks = await Db(scope).Questions.Where(q => q.ExamId == exam.Id).OrderBy(q => q.Position).Select(q => q.Marks).ToListAsync(Ct);
        Assert.Equal([0.01m, 9999.99m], marks);
    }

    // MakeQuestionMarksNullable (owner decision 2026-10-25): marks not set yet are stored as NULL, never as a default,
    // and CK_Questions_Marks still rejects 0 and below.
    [Fact]
    public async Task QuestionWithoutMarks_IsStoredAsNull()
    {
        var exam = await InsertExamAsync();

        await SaveAsync(db => db.Questions.Add(NewQuestion(exam.Id, 1, q => q.Marks = null)));

        await using var scope = factory.Services.CreateAsyncScope();
        var marks = await Db(scope).Questions.Where(q => q.ExamId == exam.Id).Select(q => q.Marks).SingleAsync(Ct);
        Assert.Null(marks);
    }

    [Fact]
    public async Task DuplicateQuestionPosition_InTheSameExam_IsRejected_ButAllowedInAnotherExam()
    {
        var exam = await InsertExamAsync();
        var otherExam = await InsertExamAsync();
        await SaveAsync(db => db.Questions.Add(NewQuestion(exam.Id, 1)));

        await AssertSaveFailsAsync(UniqueIndexViolation, db => db.Questions.Add(NewQuestion(exam.Id, 1)));

        await SaveAsync(db => db.Questions.AddRange(NewQuestion(otherExam.Id, 1), NewQuestion(exam.Id, 2)));
    }

    [Fact]
    public async Task DuplicateOptionPosition_InTheSameQuestion_IsRejected_ButAllowedInAnotherQuestion()
    {
        var exam = await InsertExamAsync();
        var question = NewQuestion(exam.Id, 1);
        var otherQuestion = NewQuestion(exam.Id, 2);
        await SaveAsync(db =>
        {
            db.Questions.AddRange(question, otherQuestion);
            db.QuestionOptions.Add(NewOption(question.Id, 1));
        });

        await AssertSaveFailsAsync(UniqueIndexViolation, db => db.QuestionOptions.Add(NewOption(question.Id, 1)));

        await SaveAsync(db => db.QuestionOptions.AddRange(NewOption(otherQuestion.Id, 1), NewOption(question.Id, 2)));
    }

    [Fact]
    public async Task TextAtTheLimit_IsAccepted_AndOneCharacterMore_IsRejectedByTheDatabase()
    {
        var instructor = await _api.CreateUserAsync(RoleNames.Instructor);
        var exam = NewExam(instructor.Id, e =>
        {
            e.Title = new string('t', ExamFieldLimits.TitleMaxLength);
            e.Description = new string('d', ExamFieldLimits.DescriptionMaxLength);
        });
        var question = NewQuestion(exam.Id, 1, q =>
        {
            q.Text = new string('q', ExamFieldLimits.QuestionTextMaxLength);
            q.ModelAnswer = new string('m', ExamFieldLimits.ModelAnswerMaxLength);
        });
        await SaveAsync(db =>
        {
            db.Exams.Add(exam);
            db.Questions.Add(question);
            db.QuestionOptions.Add(NewOption(question.Id, 1, o => o.Text = new string('o', ExamFieldLimits.OptionTextMaxLength)));
        });

        await AssertSaveFailsAsync(StringTruncated, db =>
            db.Exams.Add(NewExam(instructor.Id, e => e.Title = new string('t', ExamFieldLimits.TitleMaxLength + 1))));
        await AssertSaveFailsAsync(StringTruncated, db =>
            db.Exams.Add(NewExam(instructor.Id, e => e.Description = new string('d', ExamFieldLimits.DescriptionMaxLength + 1))));
        await AssertSaveFailsAsync(StringTruncated, db =>
            db.Questions.Add(NewQuestion(exam.Id, 2, q => q.Text = new string('q', ExamFieldLimits.QuestionTextMaxLength + 1))));
        await AssertSaveFailsAsync(StringTruncated, db =>
            db.Questions.Add(NewQuestion(exam.Id, 2, q => q.ModelAnswer = new string('m', ExamFieldLimits.ModelAnswerMaxLength + 1))));
        await AssertSaveFailsAsync(StringTruncated, db =>
            db.QuestionOptions.Add(NewOption(question.Id, 2, o => o.Text = new string('o', ExamFieldLimits.OptionTextMaxLength + 1))));
    }

    [Fact]
    public async Task QuestionOrOptionForAMissingParent_IsRejected()
    {
        await AssertSaveFailsAsync(ConstraintViolation, db => db.Questions.Add(NewQuestion(Guid.NewGuid(), 1)));
        await AssertSaveFailsAsync(ConstraintViolation, db => db.QuestionOptions.Add(NewOption(Guid.NewGuid(), 1)));
        await AssertSaveFailsAsync(ConstraintViolation, db => db.Exams.Add(NewExam(Guid.NewGuid())));
    }

    // ---- Delete behaviour ----

    [Fact]
    public async Task DeletingAnExam_DeletesItsQuestionsAndOptions_AndNothingElse()
    {
        var exam = await InsertExamAsync();
        var otherExam = await InsertExamAsync();
        var mcq = NewQuestion(exam.Id, 1);
        var essay = NewQuestion(exam.Id, 2, q => q.Type = QuestionType.Essay);
        var otherQuestion = NewQuestion(otherExam.Id, 1);
        await SaveAsync(db =>
        {
            db.Questions.AddRange(mcq, essay, otherQuestion);
            db.QuestionOptions.AddRange(NewOption(mcq.Id, 1, o => o.IsCorrect = true), NewOption(mcq.Id, 2), NewOption(otherQuestion.Id, 1));
        });

        await SaveAsync(async db => db.Exams.Remove(await db.Exams.SingleAsync(e => e.Id == exam.Id, Ct)));

        await using var scope = factory.Services.CreateAsyncScope();
        var db = Db(scope);
        Assert.False(await db.Exams.AnyAsync(e => e.Id == exam.Id, Ct));
        Assert.False(await db.Questions.AnyAsync(q => q.ExamId == exam.Id, Ct));
        Assert.False(await db.QuestionOptions.AnyAsync(o => o.QuestionId == mcq.Id || o.QuestionId == essay.Id, Ct));
        Assert.True(await db.Exams.AnyAsync(e => e.Id == otherExam.Id, Ct));
        Assert.Equal(1, await db.QuestionOptions.CountAsync(o => o.QuestionId == otherQuestion.Id, Ct));
    }

    [Fact]
    public async Task DeletingAQuestion_DeletesItsOptions()
    {
        var exam = await InsertExamAsync();
        var question = NewQuestion(exam.Id, 1);
        await SaveAsync(db =>
        {
            db.Questions.Add(question);
            db.QuestionOptions.AddRange(NewOption(question.Id, 1, o => o.IsCorrect = true), NewOption(question.Id, 2));
        });

        await SaveAsync(async db => db.Questions.Remove(await db.Questions.SingleAsync(q => q.Id == question.Id, Ct)));

        await using var scope = factory.Services.CreateAsyncScope();
        Assert.False(await Db(scope).QuestionOptions.AnyAsync(o => o.QuestionId == question.Id, Ct));
        Assert.True(await Db(scope).Exams.AnyAsync(e => e.Id == exam.Id, Ct));
    }

    [Fact]
    public async Task DeletingAUserWhoOwnsAnExam_IsRejected_AndLeavesBothInPlace()
    {
        var instructor = await _api.CreateUserAsync(RoleNames.Instructor);
        var exam = NewExam(instructor.Id);
        await SaveAsync(db => db.Exams.Add(exam));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
                Db(scope).Users.Where(u => u.Id == instructor.Id).ExecuteDeleteAsync(Ct));
            Assert.Equal(ConstraintViolation, SqlErrorNumber(exception));
        }

        await using var check = factory.Services.CreateAsyncScope();
        Assert.True(await Db(check).Users.AnyAsync(u => u.Id == instructor.Id, Ct));
        Assert.True(await Db(check).Exams.AnyAsync(e => e.Id == exam.Id, Ct));
    }

    // ---- Stored values ----

    [Fact]
    public async Task StatusAndType_AreStoredAsNames_AndDefaultsAreDraftAndWarningLimit3()
    {
        var exam = await InsertExamAsync();
        var mcq = NewQuestion(exam.Id, 1);
        var essay = NewQuestion(exam.Id, 2, q => q.Type = QuestionType.Essay);
        await SaveAsync(db => db.Questions.AddRange(mcq, essay));

        await using var scope = factory.Services.CreateAsyncScope();
        var db = Db(scope);
        Assert.Equal("Draft", await Scalar<string>(db, $"SELECT [Status] AS [Value] FROM [Exams] WHERE [Id] = {exam.Id}"));
        Assert.Equal(3, await Scalar<int>(db, $"SELECT [WarningLimit] AS [Value] FROM [Exams] WHERE [Id] = {exam.Id}"));
        Assert.Equal("Mcq", await Scalar<string>(db, $"SELECT [Type] AS [Value] FROM [Questions] WHERE [Id] = {mcq.Id}"));
        Assert.Equal("Essay", await Scalar<string>(db, $"SELECT [Type] AS [Value] FROM [Questions] WHERE [Id] = {essay.Id}"));

        await db.Exams.Where(e => e.Id == exam.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, ExamStatus.Published), Ct);
        Assert.Equal("Published", await Scalar<string>(db, $"SELECT [Status] AS [Value] FROM [Exams] WHERE [Id] = {exam.Id}"));
    }

    // ---- RowVersion ----

    [Fact]
    public async Task StaleUpdateAndStaleDelete_AreRejected_AndLeaveTheNewerDataUntouched()
    {
        var exam = await InsertExamAsync();

        await using var scopeA = factory.Services.CreateAsyncScope();
        await using var scopeB = factory.Services.CreateAsyncScope();
        await using var scopeC = factory.Services.CreateAsyncScope();
        var dbA = Db(scopeA);
        var dbB = Db(scopeB);
        var dbC = Db(scopeC);
        var examA = await dbA.Exams.SingleAsync(e => e.Id == exam.Id, Ct);
        var examB = await dbB.Exams.SingleAsync(e => e.Id == exam.Id, Ct);
        var examC = await dbC.Exams.SingleAsync(e => e.Id == exam.Id, Ct);
        var originalVersion = examA.RowVersion.ToArray();
        Assert.Equal(originalVersion, examB.RowVersion);

        // A saves first: the row gets a new version.
        examA.Title = "Changed by A";
        await dbA.SaveChangesAsync(Ct);
        Assert.NotEqual(originalVersion, examA.RowVersion);

        // B still holds the original version: its update is rejected and writes nothing.
        examB.DurationMinutes = 99;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync(Ct));
        var stored = await StoredExamAsync(exam.Id);
        Assert.Equal("Changed by A", stored.Title);
        Assert.Equal(exam.DurationMinutes, stored.DurationMinutes);
        Assert.Equal(examA.RowVersion, stored.RowVersion);

        // C also holds the original version: its delete is rejected and the row stays.
        dbC.Exams.Remove(examC);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbC.SaveChangesAsync(Ct));
        Assert.Equal(examA.RowVersion, (await StoredExamAsync(exam.Id)).RowVersion);

        // After reloading the current version, B's update goes through and the row gets another new version.
        await dbB.Entry(examB).ReloadAsync(Ct);
        examB.DurationMinutes = 99;
        await dbB.SaveChangesAsync(Ct);
        stored = await StoredExamAsync(exam.Id);
        Assert.Equal("Changed by A", stored.Title);
        Assert.Equal(99, stored.DurationMinutes);
        Assert.NotEqual(examA.RowVersion, stored.RowVersion);
        Assert.Equal(examB.RowVersion, stored.RowVersion);
    }

    // Why Steps 2.3 and 2.4 lock the exam row before changing questions: a question change alone leaves the exam's
    // RowVersion as it was.
    [Fact]
    public async Task ChangingOnlyAQuestion_LeavesTheExamRowVersionUnchanged()
    {
        var exam = await InsertExamAsync();
        var before = (await StoredExamAsync(exam.Id)).RowVersion;

        var question = NewQuestion(exam.Id, 1);
        await SaveAsync(db => db.Questions.Add(question));
        await SaveAsync(async db => (await db.Questions.SingleAsync(q => q.Id == question.Id, Ct)).Text = "Changed");

        Assert.Equal(before, (await StoredExamAsync(exam.Id)).RowVersion);
    }

    // ---- Helpers ----

    private static AppDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<AppDbContext>();

    private static Exam NewExam(Guid instructorId, Action<Exam>? change = null)
    {
        var exam = new Exam
        {
            Id = Guid.NewGuid(),
            InstructorId = instructorId,
            Title = "Schema test exam",
            StartsAt = new DateTimeOffset(2026, 12, 1, 9, 0, 0, TimeSpan.Zero),
            DurationMinutes = 60,
        };
        change?.Invoke(exam);
        return exam;
    }

    private static Question NewQuestion(Guid examId, int position, Action<Question>? change = null)
    {
        var question = new Question
        {
            Id = Guid.NewGuid(),
            ExamId = examId,
            Type = QuestionType.Mcq,
            Text = $"Question {position}",
            Marks = 1m,
            Position = position,
        };
        change?.Invoke(question);
        return question;
    }

    private static QuestionOption NewOption(Guid questionId, int position, Action<QuestionOption>? change = null)
    {
        var option = new QuestionOption
        {
            Id = Guid.NewGuid(),
            QuestionId = questionId,
            Text = $"Option {position}",
            Position = position,
        };
        change?.Invoke(option);
        return option;
    }

    private async Task<Exam> InsertExamAsync()
    {
        var instructor = await _api.CreateUserAsync(RoleNames.Instructor);
        var exam = NewExam(instructor.Id);
        await SaveAsync(db => db.Exams.Add(exam));
        return exam;
    }

    private async Task<Exam> StoredExamAsync(Guid id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await Db(scope).Exams.AsNoTracking().SingleAsync(e => e.Id == id, Ct);
    }

    private Task SaveAsync(Action<AppDbContext> change) =>
        SaveAsync(db =>
        {
            change(db);
            return Task.CompletedTask;
        });

    private async Task SaveAsync(Func<AppDbContext, Task> change)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = Db(scope);
        await change(db);
        await db.SaveChangesAsync(Ct);
    }

    // Each attempt uses its own context, so a rejected entity never stays tracked for the next save.
    private async Task AssertSaveFailsAsync(int sqlErrorNumber, Action<AppDbContext> change)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = Db(scope);
        change(db);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        Assert.Equal(sqlErrorNumber, SqlErrorNumber(exception));
    }

    private static int? SqlErrorNumber(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql)
                return sql.Number;
        }

        return null;
    }

    private async Task<List<T>> QueryAsync<T>(string sql)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await Db(scope).Database.SqlQueryRaw<T>(sql).ToListAsync(Ct);
    }

    private static async Task<T> Scalar<T>(AppDbContext db, FormattableString sql) =>
        (await db.Database.SqlQuery<T>(sql).ToListAsync(Ct)).Single();

    private static List<ColumnRow> Sorted(IEnumerable<ColumnRow> rows) =>
        [.. rows.OrderBy(r => r.TableName).ThenBy(r => r.ColumnName)];

    private static ColumnRow GuidColumn(string table, string column) =>
        new() { TableName = table, ColumnName = column, TypeName = "uniqueidentifier", MaxLength = 16, Precision = 0, Scale = 0, IsNullable = false };

    private static ColumnRow IntColumn(string table, string column) =>
        new() { TableName = table, ColumnName = column, TypeName = "int", MaxLength = 4, Precision = 10, Scale = 0, IsNullable = false };

    // nvarchar(n) stores 2 bytes per character, so max_length is twice the character limit.
    private static ColumnRow NVarCharColumn(string table, string column, int characters, bool nullable) =>
        new() { TableName = table, ColumnName = column, TypeName = "nvarchar", MaxLength = characters * 2, Precision = 0, Scale = 0, IsNullable = nullable };

    private static ColumnRow DateTimeOffsetColumn(string table, string column, bool nullable) =>
        new() { TableName = table, ColumnName = column, TypeName = "datetimeoffset", MaxLength = 10, Precision = 34, Scale = 7, IsNullable = nullable };

    private sealed record ColumnRow
    {
        public string TableName { get; set; } = "";
        public string ColumnName { get; set; } = "";
        public string TypeName { get; set; } = "";
        public int MaxLength { get; set; }
        public int Precision { get; set; }
        public int Scale { get; set; }
        public bool IsNullable { get; set; }
    }

    private sealed record ForeignKeyRow
    {
        public string Name { get; set; } = "";
        public string TableName { get; set; } = "";
        public string ReferencedTable { get; set; } = "";
        public string DeleteAction { get; set; } = "";
        public string Columns { get; set; } = "";
    }

    private sealed record IndexRow
    {
        public string Name { get; set; } = "";
        public string TableName { get; set; } = "";
        public bool IsUnique { get; set; }
        public bool IsPrimaryKey { get; set; }
        public string Columns { get; set; } = "";
    }

    private sealed record CheckRow
    {
        public string Name { get; set; } = "";
        public string TableName { get; set; } = "";
        public string Definition { get; set; } = "";
    }
}
