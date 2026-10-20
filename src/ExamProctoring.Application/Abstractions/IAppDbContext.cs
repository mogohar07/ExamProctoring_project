using ExamProctoring.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace ExamProctoring.Application.Abstractions;

// How Application uses EF Core, instead of repositories (§4.2). Implemented by AppDbContext.
public interface IAppDbContext
{
    DbSet<ApplicationUser> Users { get; }

    // For explicit transactions that span several saves (for example user + role at sign-up, §6.4).
    DatabaseFacade Database { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
