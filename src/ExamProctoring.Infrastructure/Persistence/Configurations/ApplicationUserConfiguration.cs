using ExamProctoring.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExamProctoring.Infrastructure.Persistence.Configurations;

// Custom AspNetUsers columns and indexes (§5.2, §5.3). Identity's own columns keep their defaults
// (Email, UserName and their normalized forms are nvarchar(256)).
public class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.Property(u => u.FullName).HasMaxLength(150).IsRequired();
        builder.Property(u => u.AccountStatus).HasConversion<string>().HasMaxLength(30);
        builder.Property(u => u.StatusReason).HasMaxLength(500);

        // Identity's "EmailIndex" is not unique. Email is unique across every role (D12), and the
        // database is the final guard when two sign-ups with the same email arrive together (§6.4).
        builder.HasIndex(u => u.NormalizedEmail)
            .HasDatabaseName("EmailIndex")
            .IsUnique()
            .HasFilter("[NormalizedEmail] IS NOT NULL");

        // Admin account lists.
        builder.HasIndex(u => new { u.AccountStatus, u.CreatedAt });

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(u => u.CreatedById)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(u => u.StatusChangedById)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
