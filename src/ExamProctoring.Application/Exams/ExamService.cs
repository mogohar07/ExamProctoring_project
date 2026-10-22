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

        var exam = new Exam
        {
            InstructorId = currentUser.UserId,
            Title = request.Title!.Trim(),
            Description = Normalize(request.Description),
            StartsAt = startsAt,
            DurationMinutes = request.DurationMinutes!.Value,
            Status = ExamStatus.Draft,
            WarningLimit = settings.WarningLimit,
        };
        db.Exams.Add(exam);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Instructor {InstructorId} created exam {ExamId}.", currentUser.UserId, exam.Id);

        return Result<ExamResponse>.Success(ExamResponse.From(exam, now));
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

    private IQueryable<Exam> OwnExams() => db.Exams.Where(e => e.InstructorId == currentUser.UserId);

    // A write matched nothing: 404 when the exam is not the caller's (or does not exist), otherwise its state or
    // start time forbids the change (409).
    private async Task<AppError> RefusalAsync(Guid id, CancellationToken cancellationToken) =>
        await OwnExams().AsNoTracking().AnyAsync(e => e.Id == id, cancellationToken)
            ? new AppError(ErrorCodes.ExamNotEditable, "The exam can no longer be changed in its current state.")
            : ExamNotFound();

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
