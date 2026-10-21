using ExamProctoring.Domain.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace ExamProctoring.Application.Abstractions;

// How Application uses EF Core, instead of repositories (§4.2). Implemented by AppDbContext.
public interface IAppDbContext
{
    DbSet<ApplicationUser> Users { get; }

    // Identity's role tables, for queries that need each account's role (admin account list, D23).
    DbSet<IdentityRole<Guid>> Roles { get; }

    DbSet<IdentityUserRole<Guid>> UserRoles { get; }

    DbSet<RefreshToken> RefreshTokens { get; }

    // For explicit transactions that span several saves (for example user + role at sign-up, §6.4).
    DatabaseFacade Database { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
