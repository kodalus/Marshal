using Marshal.Domain.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Marshal.Infrastructure.Data.Configurations;

public sealed class ActivityEntryConfiguration : IEntityTypeConfiguration<ActivityEntry>
{
    public void Configure(EntityTypeBuilder<ActivityEntry> builder)
    {
        builder.ToTable("ActivityEntries");
        builder.HasKey(w => w.Id);

        builder.Property(w => w.Operation).IsRequired().HasMaxLength(100);
        builder.Property(w => w.Outcome).IsRequired().HasMaxLength(500);

        // Treść wyjątku bywa długa, a ucięta w połowie przestaje być odpowiedzią.
        builder.Property(w => w.Detail).HasMaxLength(4000);

        // Czytamy wyłącznie od najnowszych — indeks jest tu całą wydajnością ekranu.
        builder.HasIndex(w => w.At);
    }
}
