using ExamProctoring.Domain.Exams;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamProctoring.Infrastructure.Persistence.Configurations;

// §5.1-§5.3. Question.SourceGeneratedQuestionId arrives with GeneratedQuestion in Phase 3.
public class QuestionConfiguration : IEntityTypeConfiguration<Question>
{
    public void Configure(EntityTypeBuilder<Question> builder)
    {
        builder.ToTable(table => table.HasCheckConstraint("CK_Questions_Marks", "[Marks] > 0"));

        builder.Property(q => q.Type).HasConversion<string>().HasMaxLength(30);
        builder.Property(q => q.Text).HasMaxLength(ExamFieldLimits.QuestionTextMaxLength).IsRequired();
        builder.Property(q => q.ModelAnswer).HasMaxLength(ExamFieldLimits.ModelAnswerMaxLength);
        builder.Property(q => q.Marks).HasPrecision(6, 2);

        builder.HasIndex(q => new { q.ExamId, q.Position }).IsUnique();

        // §5.1 cascade Exam → Question. The database cannot tell Draft from Published, so the application allows hard
        // deletion of Draft exams only.
        builder.HasOne<Exam>()
            .WithMany()
            .HasForeignKey(q => q.ExamId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
