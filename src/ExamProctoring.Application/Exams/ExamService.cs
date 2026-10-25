using ExamProctoring.Application.Abstractions;
using ExamProctoring.Application.Common;
using ExamProctoring.Domain.Exams;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ExamProctoring.Application.Exams;

// Instructor exam management (§7, D25-D30): create, list, read, update and delete the caller's own exams. Ownership
// is part of every query (§6.7): another instructor's exam is answered like an unknown one.
//
// Writes to an existing exam take its row lock as their first statement, filtered by the owner and by the state the
// operation allows, so writes to one exam never overlap and the state is checked under the lock (Phase 2 plan, R2).
public sealed class ExamService(
    IAppDbContext db,
    IValidator<ExamRequest> examValidator,
    IValidator<ExamListQuery> listValidator,
    IValidator<QuestionRequest> questionValidator,
    IExamSettings settings,
    IClock clock,
    ICurrentUser currentUser,
    ILogger<ExamService> logger)
{
    // POST /api/exams (D25, D26). A new Draft exam; its warning limit is copied from the general setting.
    public async Task<Result<ExamResponse>> CreateAsync(ExamRequest request, CancellationToken cancellationToken)
    {
        if (await ValidationFailedAsync(examValidator, request, cancellationToken) is { } invalid)
            return Result<ExamResponse>.Failure(invalid);

        ExamStartTime.TryParse(request.StartsAt, out var startsAt);
        var now = clock.UtcNow;
        if (startsAt <= now)
            return Result<ExamResponse>.Failure(StartsAtNotInFuture());

        var exam = NewDraft(request, startsAt);
        db.Exams.Add(exam);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Instructor {InstructorId} created exam {ExamId}.", currentUser.UserId, exam.Id);

        return Result<ExamResponse>.Success(ExamResponse.From(exam, now));
    }

    // POST /api/exams/with-questions (owner decision 2026-10-25). The reviewed questions and the exam details are saved
    // as a new Draft exam in one transaction: every field is checked first with the rules of POST /api/exams and
    // POST /api/exams/{id}/questions, and if anything is invalid nothing is saved. Questions may have no marks, because
    // the exam is a Draft; publishing requires them (PublicationCheck). The exam is never published here.
    public async Task<Result<DraftExamResponse>> CreateWithQuestionsAsync(DraftExamRequest request, CancellationToken cancellationToken)
    {
        var examRequest = request.ToExamRequest();
        var fieldErrors = new Dictionary<string, string[]>();
        if (await ValidationFailedAsync(examValidator, examRequest, cancellationToken) is { FieldErrors: { } examErrors })
        {
            foreach (var (field, messages) in examErrors)
                fieldErrors[field] = messages;
        }

        var questions = request.Questions ?? [];
        if (questions.Count == 0)
            fieldErrors["questions"] = ["At least one question is required."];
        else if (questions.Count > DraftExamRequest.MaxQuestions)
            fieldErrors["questions"] = [$"At most {DraftExamRequest.MaxQuestions} questions can be saved at once."];
        else
        {
            for (var i = 0; i < questions.Count; i++)
            {
                if (questions[i] is not { } question)
                {
                    fieldErrors[$"questions[{i}]"] = ["Question is required."];
                    continue;
                }
                foreach (var (field, messages) in await QuestionService.FieldErrorsAsync(questionValidator, question, cancellationToken))
                    fieldErrors[$"questions[{i}].{field}"] = messages;
            }
        }

        if (fieldErrors.Count > 0)
            return Result<DraftExamResponse>.Failure(new AppError(ErrorCodes.ValidationFailed, "One or more fields are invalid.", fieldErrors));

        ExamStartTime.TryParse(examRequest.StartsAt, out var startsAt);
        var now = clock.UtcNow;
        if (startsAt <= now)
            return Result<DraftExamResponse>.Failure(StartsAtNotInFuture());

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var exam = NewDraft(examRequest, startsAt);
        db.Exams.Add(exam);

        var saved = new List<(Question Question, List<QuestionOption> Options)>();
        for (var i = 0; i < questions.Count; i++)
        {
            var question = new Question { ExamId = exam.Id, Position = i + 1 };
            QuestionService.Apply(question, questions[i]!);
            db.Questions.Add(question);
            var options = QuestionService.NewOptions(question, questions[i]!);
            db.QuestionOptions.AddRange(options);
            saved.Add((question, options));
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Instructor {InstructorId} created Draft exam {ExamId} with {Count} questions ({Unmarked} without marks).",
            currentUser.UserId, exam.Id, saved.Count, saved.Count(s => s.Question.Marks is null));

        return Result<DraftExamResponse>.Success(new DraftExamResponse(
            ExamResponse.From(exam, now),
            saved.Select(s => QuestionResponse.From(s.Question, s.Options)).ToList()));
    }

    // GET /api/exams (D29). The caller's exams, newest start first, one page at a time.
    public async Task<Result<ExamListResponse>> ListAsync(ExamListQuery query, CancellationToken cancellationToken)
    {
        if (await ValidationFailedAsync(listValidator, query, cancellationToken) is { } invalid)
            return Result<ExamListResponse>.Failure(invalid);

        var page = query.Page ?? 1;
        var pageSize = query.PageSize ?? ExamListQuery.DefaultPageSize;

        var exams = OwnExams().AsNoTracking();
        if (query.Status is not null)
        {
            var status = ExamListQuery.ParseStatus(query.Status);
            exams = exams.Where(e => e.Status == status);
        }

        var totalCount = await exams.CountAsync(cancellationToken);
        var now = clock.UtcNow;

        // A page that starts beyond the last exam is empty; so is one too far out to compute an offset for.
        var skip = (long)(page - 1) * pageSize;
        IReadOnlyList<ExamResponse> items = skip >= totalCount
            ? []
            : (await exams
                    // Newest start first; the id breaks ties so that the order is deterministic.
                    .OrderByDescending(e => e.StartsAt)
                    .ThenByDescending(e => e.Id)
                    .Skip((int)skip)
                    .Take(pageSize)
                    .ToListAsync(cancellationToken))
                .Select(e => ExamResponse.From(e, now))
                .ToList();

        return Result<ExamListResponse>.Success(new ExamListResponse(items, page, pageSize, totalCount));
    }

    // GET /api/exams/{id}. An unknown id, an id that is not a Guid and another instructor's exam are all 404.
    public async Task<Result<ExamResponse>> GetAsync(string examId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(examId, out var id))
            return Result<ExamResponse>.Failure(ExamNotFound());

        var exam = await OwnExams().AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, cancellationToken);

        return exam is null
            ? Result<ExamResponse>.Failure(ExamNotFound())
            : Result<ExamResponse>.Success(ExamResponse.From(exam, clock.UtcNow));
    }

    // PUT /api/exams/{id} (D25, D27). Replaces title, description, start time and duration of a Draft or Published
    // exam that has not started; a changed start time must be in the future. The status, publication time and
    // warning limit are not changed. Of two overlapping updates, the later one wins (RowVersion stays internal, D28).
    public async Task<Result<ExamResponse>> UpdateAsync(string examId, ExamRequest request, CancellationToken cancellationToken)
    {
        if (await ValidationFailedAsync(examValidator, request, cancellationToken) is { } invalid)
            return Result<ExamResponse>.Failure(invalid);

        if (!Guid.TryParse(examId, out var id))
            return Result<ExamResponse>.Failure(ExamNotFound());

        ExamStartTime.TryParse(request.StartsAt, out var startsAt);
        var now = clock.UtcNow;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The exam row lock, only while the exam is the caller's, Draft or Published, and has not started (D27).
        var locked = await OwnExams()
            .Where(e => e.Id == id
                && (e.Status == ExamStatus.Draft || e.Status == ExamStatus.Published)
                && e.StartsAt > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(e => e.Title, e => e.Title), cancellationToken);
        if (locked == 0)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return Result<ExamResponse>.Failure(await RefusalAsync(id, cancellationToken));
        }

        // Read under the lock, so it is current and nothing else changes it before the commit.
        var exam = await db.Exams.SingleAsync(e => e.Id == id, cancellationToken);

        if (startsAt != exam.StartsAt && startsAt <= now)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return Result<ExamResponse>.Failure(StartsAtNotInFuture());
        }

        exam.Title = request.Title!.Trim();
        exam.Description = Normalize(request.Description);
        exam.StartsAt = startsAt;
        exam.DurationMinutes = request.DurationMinutes!.Value;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Instructor {InstructorId} updated exam {ExamId}.", currentUser.UserId, exam.Id);

        return Result<ExamResponse>.Success(ExamResponse.From(exam, now));
    }

    // DELETE /api/exams/{id} (D30). Only a Draft is deleted, even after its start time (O4), together with its
    // questions and options (cascade). Published and Cancelled exams are never hard-deleted.
    public async Task<Result<bool>> DeleteAsync(string examId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(examId, out var id))
            return Result<bool>.Failure(ExamNotFound());

        // One statement: it takes the exam row lock and re-checks the status under it, so it waits for any write in
        // progress on the exam and never deletes an exam that was just published.
        var deleted = await OwnExams()
            .Where(e => e.Id == id && e.Status == ExamStatus.Draft)
            .ExecuteDeleteAsync(cancellationToken);
        if (deleted == 0)
            return Result<bool>.Failure(await RefusalAsync(id, cancellationToken));

        logger.LogInformation("Instructor {InstructorId} deleted exam {ExamId}.", currentUser.UserId, id);

        return Result<bool>.Success(true);
    }

    // POST /api/exams/{id}/publish (D34, D36). A Draft that has not started, with at least one question and every
    // question valid under the current rules and MCQ maximum, becomes Published at the current time.
    public async Task<Result<ExamResponse>> PublishAsync(string examId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(examId, out var id))
            return Result<ExamResponse>.Failure(ExamNotFound());

        var now = clock.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await LockAsync(id, ExamStatus.Draft, now, cancellationToken))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return Result<ExamResponse>.Failure(await StateRefusalAsync(id, ErrorCodes.ExamNotPublishable, exam => exam.Status switch
            {
                ExamStatus.Published => "The exam is already published.",
                ExamStatus.Cancelled => "A cancelled exam cannot be published.",
                _ => "The start time has passed.",
            }, cancellationToken));
        }

        // The whole exam, read under the lock (D34 clarification: publication checks every question).
        var questions = await db.Questions.AsNoTracking().Where(q => q.ExamId == id).ToListAsync(cancellationToken);
        var options = await db.QuestionOptions.AsNoTracking()
            .Where(o => db.Questions.Any(q => q.Id == o.QuestionId && q.ExamId == id))
            .ToListAsync(cancellationToken);
        if (PublicationCheck.FirstProblem(questions, options, settings.MaxMcqOptions) is { } problem)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return Result<ExamResponse>.Failure(new AppError(ErrorCodes.ExamNotPublishable, problem));
        }

        var published = await db.Exams.SingleAsync(e => e.Id == id, cancellationToken);
        published.Status = ExamStatus.Published;
        published.PublishedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Instructor {InstructorId} published exam {ExamId}.", currentUser.UserId, id);

        return Result<ExamResponse>.Success(ExamResponse.From(published, now));
    }

    // POST /api/exams/{id}/unpublish (§2.2, D34, D36). A Published exam that has not started returns to Draft and loses
    // its PublishedAt. The "no attempt" condition needs attempts, which arrive in Phase 4; until then none can exist.
    public Task<Result<ExamResponse>> UnpublishAsync(string examId, CancellationToken cancellationToken) =>
        ChangeStatusAsync(examId, ExamStatus.Draft, ErrorCodes.ExamNotUnpublishable, exam => exam.Status switch
        {
            ExamStatus.Draft => "The exam is not published.",
            ExamStatus.Cancelled => "A cancelled exam cannot be unpublished.",
            _ => "The start time has passed.",
        }, cancellationToken);

    // POST /api/exams/{id}/cancel (D30, D36). A Published exam that has not started becomes Cancelled, which is final.
    // No cancellation time or reason is stored; PublishedAt is kept.
    public Task<Result<ExamResponse>> CancelAsync(string examId, CancellationToken cancellationToken) =>
        ChangeStatusAsync(examId, ExamStatus.Cancelled, ErrorCodes.ExamNotCancellable, exam => exam.Status switch
        {
            ExamStatus.Draft => "Only a published exam can be cancelled.",
            ExamStatus.Cancelled => "The exam is already cancelled.",
            _ => "The start time has passed.",
        }, cancellationToken);

    // Unpublish and cancel: both start from a Published exam that has not started.
    private async Task<Result<ExamResponse>> ChangeStatusAsync(
        string examId, ExamStatus newStatus, string refusalCode, Func<Exam, string> refusalMessage,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(examId, out var id))
            return Result<ExamResponse>.Failure(ExamNotFound());

        var now = clock.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await LockAsync(id, ExamStatus.Published, now, cancellationToken))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return Result<ExamResponse>.Failure(await StateRefusalAsync(id, refusalCode, refusalMessage, cancellationToken));
        }

        var exam = await db.Exams.SingleAsync(e => e.Id == id, cancellationToken);
        exam.Status = newStatus;
        if (newStatus == ExamStatus.Draft)
            exam.PublishedAt = null;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Instructor {InstructorId} changed exam {ExamId} to {Status}.", currentUser.UserId, id, newStatus);

        return Result<ExamResponse>.Success(ExamResponse.From(exam, now));
    }

    // The exam row lock for a status change: only while the exam is the caller's, has the given status and has not
    // started.
    private async Task<bool> LockAsync(Guid id, ExamStatus status, DateTimeOffset now, CancellationToken cancellationToken) =>
        await OwnExams()
            .Where(e => e.Id == id && e.Status == status && e.StartsAt > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(e => e.Title, e => e.Title), cancellationToken) > 0;

    // A status change matched nothing: 404 when the exam is not the caller's, otherwise the operation's 409 with the
    // reason its state or start time gives.
    private async Task<AppError> StateRefusalAsync(
        Guid id, string code, Func<Exam, string> message, CancellationToken cancellationToken)
    {
        var exam = await OwnExams().AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        return exam is null ? ExamNotFound() : new AppError(code, message(exam));
    }

    private IQueryable<Exam> OwnExams() => db.Exams.Where(e => e.InstructorId == currentUser.UserId);

    // A write matched nothing: 404 when the exam is not the caller's (or does not exist), otherwise its state or
    // start time forbids the change (409).
    private async Task<AppError> RefusalAsync(Guid id, CancellationToken cancellationToken) =>
        await OwnExams().AsNoTracking().AnyAsync(e => e.Id == id, cancellationToken)
            ? new AppError(ErrorCodes.ExamNotEditable, "The exam can no longer be changed in its current state.")
            : ExamNotFound();

    // A new Draft exam of the caller, from validated fields; its warning limit is copied from the general setting (D26).
    private Exam NewDraft(ExamRequest request, DateTimeOffset startsAt) => new()
    {
        InstructorId = currentUser.UserId,
        Title = request.Title!.Trim(),
        Description = Normalize(request.Description),
        StartsAt = startsAt,
        DurationMinutes = request.DurationMinutes!.Value,
        Status = ExamStatus.Draft,
        WarningLimit = settings.WarningLimit,
    };

    // Trimmed; blank counts as none.
    private static string? Normalize(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static AppError StartsAtNotInFuture() =>
        new(ErrorCodes.ValidationFailed, "One or more fields are invalid.",
            new Dictionary<string, string[]> { [nameof(ExamRequest.StartsAt)] = ["Start time must be in the future."] });

    private static AppError ExamNotFound() => new(ErrorCodes.ExamNotFound, "The exam was not found.");

    private static async Task<AppError?> ValidationFailedAsync<T>(
        IValidator<T> fieldValidator, T request, CancellationToken cancellationToken)
    {
        var fieldErrors = (await fieldValidator.ValidateAsync(request, cancellationToken)).Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).Distinct().ToArray());

        return fieldErrors.Count > 0
            ? new AppError(ErrorCodes.ValidationFailed, "One or more fields are invalid.", fieldErrors)
            : null;
    }
}
