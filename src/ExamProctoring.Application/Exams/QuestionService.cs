using ExamProctoring.Application.Abstractions;
using ExamProctoring.Application.Common;
using ExamProctoring.Domain.Exams;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ExamProctoring.Application.Exams;

// Questions of the caller's own exams (§7, D27, D31-D35). The owner reads them at any time (S4). Every write takes the
// exam row lock as its first statement, like ExamService.UpdateAsync: only while the exam is the caller's, Draft or
// Published, and has not started. So writes to one exam never overlap, and positions, the last-question rule and the
// exam's state are all read under the lock (Phase 2 plan, R2).
public sealed class QuestionService(
    IAppDbContext db,
    IValidator<QuestionRequest> questionValidator,
    IClock clock,
    ICurrentUser currentUser,
    ILogger<QuestionService> logger)
{
    // GET /api/exams/{id}/questions (S2, S4). All questions in position order, each with its options.
    public async Task<Result<QuestionListResponse>> ListAsync(string examId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(examId, out var id))
            return Result<QuestionListResponse>.Failure(ExamNotFound());

        // One statement for questions and options, so they come from the same moment.
        var rows = await (
                from question in db.Questions
                join exam in OwnExams() on question.ExamId equals exam.Id
                where exam.Id == id
                join option in db.QuestionOptions on question.Id equals option.QuestionId into options
                from option in options.DefaultIfEmpty()
                select new { Question = question, Option = option })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        if (rows.Count == 0 && !await OwnExams().AnyAsync(e => e.Id == id, cancellationToken))
            return Result<QuestionListResponse>.Failure(ExamNotFound());

        var items = rows
            .GroupBy(row => row.Question.Id)
            .Select(group => QuestionResponse.From(
                group.First().Question,
                group.Select(row => row.Option).OfType<QuestionOption>()))
            .OrderBy(question => question.Position)
            .ToList();

        return Result<QuestionListResponse>.Success(new QuestionListResponse(items));
    }

    // POST /api/exams/{id}/questions (D33). The question is appended after the exam's last question.
    public async Task<Result<QuestionResponse>> CreateAsync(string examId, QuestionRequest request, CancellationToken cancellationToken)
    {
        if (await ValidationFailedAsync(request, cancellationToken) is { } invalid)
            return Result<QuestionResponse>.Failure(invalid);

        if (!Guid.TryParse(examId, out var id))
            return Result<QuestionResponse>.Failure(ExamNotFound());

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await LockEditableExamAsync(id, cancellationToken))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return Result<QuestionResponse>.Failure(
                await OwnExams().AnyAsync(e => e.Id == id, cancellationToken) ? ExamNotEditable() : ExamNotFound());
        }

        var lastPosition = await db.Questions
            .Where(q => q.ExamId == id)
            .MaxAsync(q => (int?)q.Position, cancellationToken) ?? 0;

        var question = new Question { ExamId = id, Position = lastPosition + 1 };
        Apply(question, request);
        db.Questions.Add(question);
        var options = AddOptions(question, request);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Instructor {InstructorId} added question {QuestionId} to exam {ExamId}.",
            currentUser.UserId, question.Id, id);

        return Result<QuestionResponse>.Success(QuestionResponse.From(question, options));
    }

    // PUT /api/questions/{id} (D33). Full replacement: the type, text, marks, model answer and the whole set of options.
    // The question keeps its id, exam and position; its options are replaced by new ones. If anything is invalid,
    // nothing changes.
    public async Task<Result<QuestionResponse>> UpdateAsync(string questionId, QuestionRequest request, CancellationToken cancellationToken)
    {
        if (await ValidationFailedAsync(request, cancellationToken) is { } invalid)
            return Result<QuestionResponse>.Failure(invalid);

        if (!Guid.TryParse(questionId, out var id) || await ExamOfOwnQuestionAsync(id, cancellationToken) is not { } examId)
            return Result<QuestionResponse>.Failure(QuestionNotFound());

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await LockEditableExamAsync(examId, cancellationToken))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return Result<QuestionResponse>.Failure(await RefusalAsync(examId, cancellationToken));
        }

        // Read under the lock: the question may have been deleted while this request waited.
        var question = await db.Questions.SingleOrDefaultAsync(q => q.Id == id && q.ExamId == examId, cancellationToken);
        if (question is null)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return Result<QuestionResponse>.Failure(QuestionNotFound());
        }

        await db.QuestionOptions.Where(o => o.QuestionId == id).ExecuteDeleteAsync(cancellationToken);
        Apply(question, request);
        var options = AddOptions(question, request);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Instructor {InstructorId} updated question {QuestionId} of exam {ExamId}.",
            currentUser.UserId, id, examId);

        return Result<QuestionResponse>.Success(QuestionResponse.From(question, options));
    }

    // DELETE /api/questions/{id} (D33, D34, S5). The question and its options are removed and the later questions move
    // up, so positions stay 1..n. A Published exam keeps at least one question; other questions are not re-checked.
    public async Task<Result<bool>> DeleteAsync(string questionId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(questionId, out var id) || await ExamOfOwnQuestionAsync(id, cancellationToken) is not { } examId)
            return Result<bool>.Failure(QuestionNotFound());

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await LockEditableExamAsync(examId, cancellationToken))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return Result<bool>.Failure(await RefusalAsync(examId, cancellationToken));
        }

        var position = await db.Questions
            .Where(q => q.Id == id && q.ExamId == examId)
            .Select(q => (int?)q.Position)
            .SingleOrDefaultAsync(cancellationToken);
        if (position is null)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return Result<bool>.Failure(QuestionNotFound());
        }

        var status = await db.Exams.Where(e => e.Id == examId).Select(e => e.Status).SingleAsync(cancellationToken);
        if (status == ExamStatus.Published && await db.Questions.CountAsync(q => q.ExamId == examId, cancellationToken) == 1)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return Result<bool>.Failure(new AppError(ErrorCodes.ExamNotEditable, "A published exam must keep at least one question."));
        }

        // The options go with the question (cascade). One statement moves every later question up by one, so the unique
        // (ExamId, Position) index is never broken part-way.
        await db.Questions.Where(q => q.Id == id).ExecuteDeleteAsync(cancellationToken);
        await db.Questions
            .Where(q => q.ExamId == examId && q.Position > position)
            .ExecuteUpdateAsync(setters => setters.SetProperty(q => q.Position, q => q.Position - 1), cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Instructor {InstructorId} deleted question {QuestionId} of exam {ExamId}.",
            currentUser.UserId, id, examId);

        return Result<bool>.Success(true);
    }

    private IQueryable<Exam> OwnExams() => db.Exams.Where(e => e.InstructorId == currentUser.UserId);

    // The exam of one of the caller's questions, or null when the question is unknown or another instructor's (S3).
    private Task<Guid?> ExamOfOwnQuestionAsync(Guid questionId, CancellationToken cancellationToken) =>
        (from question in db.Questions
         join exam in OwnExams() on question.ExamId equals exam.Id
         where question.Id == questionId
         select (Guid?)exam.Id)
        .SingleOrDefaultAsync(cancellationToken);

    // The exam row lock (R2, D27): a no-op update that matches only while the exam is the caller's, Draft or Published,
    // and has not started.
    private async Task<bool> LockEditableExamAsync(Guid examId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var locked = await OwnExams()
            .Where(e => e.Id == examId
                && (e.Status == ExamStatus.Draft || e.Status == ExamStatus.Published)
                && e.StartsAt > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(e => e.Title, e => e.Title), cancellationToken);
        return locked > 0;
    }

    // The lock on a question's exam was refused: the exam's state or start time forbids the change (409), or the exam,
    // and with it the question, is gone (404).
    private async Task<AppError> RefusalAsync(Guid examId, CancellationToken cancellationToken) =>
        await OwnExams().AnyAsync(e => e.Id == examId, cancellationToken) ? ExamNotEditable() : QuestionNotFound();

    // Text and model answer trimmed (D31); a blank model answer is stored as null.
    private static void Apply(Question question, QuestionRequest request)
    {
        question.Type = QuestionRequest.ParseType(request.Type)!.Value;
        question.Text = request.Text!.Trim();
        question.Marks = NormalizeMarks(request.Marks!.Value);
        question.ModelAnswer = string.IsNullOrWhiteSpace(request.ModelAnswer) ? null : request.ModelAnswer.Trim();
    }

    // The options of an MCQ in the order given, numbered from 1, with trimmed text. An Essay has none.
    private List<QuestionOption> AddOptions(Question question, QuestionRequest request)
    {
        if (question.Type != QuestionType.Mcq)
            return [];

        var options = request.Options!
            .Select((option, index) => new QuestionOption
            {
                QuestionId = question.Id,
                Text = option!.Text!.Trim(),
                IsCorrect = option.IsCorrect ?? false,
                Position = index + 1,
            })
            .ToList();
        db.QuestionOptions.AddRange(options);
        return options;
    }

    // Two decimal places, as the column stores them, so 1.5, 1.50 and 1.500 are all returned as 1.50. The value itself
    // never changes: the validator has already rejected anything with more than two decimal places.
    private static decimal NormalizeMarks(decimal marks) => decimal.Round(marks + 0.00m, QuestionRules.MarksDecimalPlaces);

    private static AppError ExamNotFound() => new(ErrorCodes.ExamNotFound, "The exam was not found.");

    private static AppError ExamNotEditable() =>
        new(ErrorCodes.ExamNotEditable, "The exam can no longer be changed in its current state.");

    private static AppError QuestionNotFound() => new(ErrorCodes.QuestionNotFound, "The question was not found.");

    // Field names as the JSON request spells them, including nested ones such as options[1].text.
    private async Task<AppError?> ValidationFailedAsync(QuestionRequest request, CancellationToken cancellationToken)
    {
        var fieldErrors = (await questionValidator.ValidateAsync(request, cancellationToken)).Errors
            .GroupBy(e => CamelCase(e.PropertyName))
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).Distinct().ToArray());

        return fieldErrors.Count > 0
            ? new AppError(ErrorCodes.ValidationFailed, "One or more fields are invalid.", fieldErrors)
            : null;
    }

    private static string CamelCase(string propertyName) =>
        string.Join('.', propertyName.Split('.').Select(part => part.Length == 0 ? part : char.ToLowerInvariant(part[0]) + part[1..]));
}
