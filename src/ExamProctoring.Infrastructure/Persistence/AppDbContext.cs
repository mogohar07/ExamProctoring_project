using Microsoft.EntityFrameworkCore;

namespace ExamProctoring.Infrastructure.Persistence;

// Phase 0: no entities yet. Phase 1 changes the base class to
// IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid> (architecture §4.3).
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Picks up one IEntityTypeConfiguration<T> per entity from Persistence/Configurations.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
