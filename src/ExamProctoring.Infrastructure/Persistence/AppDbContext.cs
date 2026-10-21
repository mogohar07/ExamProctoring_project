using ExamProctoring.Application.Abstractions;
using ExamProctoring.Domain.Exams;
using ExamProctoring.Domain.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ExamProctoring.Infrastructure.Persistence;

// Identity tables (AspNetUsers, AspNetRoles, AspNetUserRoles, ...) with Guid keys, plus the application's own entities.
public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options), IAppDbContext
{
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<Exam> Exams => Set<Exam>();

    public DbSet<Question> Questions => Set<Question>();

    public DbSet<QuestionOption> QuestionOptions => Set<QuestionOption>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Must run first: it defines the Identity tables that the configurations below refine.
        base.OnModelCreating(modelBuilder);

        // Picks up one IEntityTypeConfiguration<T> per entity from Persistence/Configurations.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
