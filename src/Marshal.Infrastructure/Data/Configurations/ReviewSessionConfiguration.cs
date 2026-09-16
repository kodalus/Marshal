using Marshal.Domain.Review;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Marshal.Infrastructure.Data.Configurations;

public sealed class ReviewSessionConfiguration : IEntityTypeConfiguration<ReviewSession>
{
    public void Configure(EntityTypeBuilder<ReviewSession> builder)
    {
        builder.ToTable("ReviewSessions");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.StartedAt).IsRequired();
        builder.Property(r => r.CurrentStep).IsRequired();
        builder.Property(r => r.ProcessedIdsJson).IsRequired();
        builder.Property(r => r.CreatedAt).IsRequired();
        builder.Property(r => r.UpdatedAt).IsRequired();
        builder.Property(r => r.Deleted).IsRequired();

        // Liczone ze zbioru, nie przechowywane — inaczej dałoby się mieć licznik
        // rozjechany ze zbiorem, którego dotyczy.
        builder.Ignore(r => r.ProcessedCount);
        builder.Ignore(r => r.IsCompleted);

        builder.HasIndex(r => r.CompletedAt);
    }
}
