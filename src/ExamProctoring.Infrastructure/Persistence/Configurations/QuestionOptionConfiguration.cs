using ExamProctoring.Domain.Exams;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamProctoring.Infrastructure.Persistence.Configurations;

// §5.1-§5.3. "Exactly one correct option" is a Domain rule (QuestionRules), not a database constraint.
public class QuestionOptionConfiguration : IEntityTypeConfiguration<QuestionOption>
{
    public void Configure(EntityTypeBuilder<QuestionOption> builder)
    {
        builder.Property(o => o.Text).HasMaxLength(ExamFieldLimits.OptionTextMaxLength).IsRequired();

        builder.HasIndex(o => new { o.QuestionId, o.Position }).IsUnique();

        // §5.1 cascade Question → QuestionOption.
        builder.HasOne<Question>()
            .WithMany()
            .HasForeignKey(o => o.QuestionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
