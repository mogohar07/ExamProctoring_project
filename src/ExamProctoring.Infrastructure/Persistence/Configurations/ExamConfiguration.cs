using ExamProctoring.Domain.Exams;
using ExamProctoring.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamProctoring.Infrastructure.Persistence.Configurations;

// §5.1-§5.3. Enum values are checked by the application, not by the database.
public class ExamConfiguration : IEntityTypeConfiguration<Exam>
{
    public void Configure(EntityTypeBuilder<Exam> builder)
    {
        builder.ToTable(table =>
        {
            table.HasCheckConstraint("CK_Exams_DurationMinutes", "[DurationMinutes] > 0");
            table.HasCheckConstraint("CK_Exams_WarningLimit", "[WarningLimit] > 0");
        });

        builder.Property(e => e.Title).HasMaxLength(ExamFieldLimits.TitleMaxLength).IsRequired();
        builder.Property(e => e.Description).HasMaxLength(ExamFieldLimits.DescriptionMaxLength);
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(e => e.RowVersion).IsRowVersion();

        // Calculated from StartsAt and DurationMinutes (§2.2); not a column.
        builder.Ignore(e => e.EndsAt);

        // Instructor exam lists.
        builder.HasIndex(e => new { e.InstructorId, e.StartsAt });

        // The owner; a user who owns exams cannot be deleted.
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(e => e.InstructorId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
