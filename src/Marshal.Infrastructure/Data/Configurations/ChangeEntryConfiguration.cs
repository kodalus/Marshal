using Marshal.Domain.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Marshal.Infrastructure.Data.Configurations;

public sealed class ChangeEntryConfiguration : IEntityTypeConfiguration<ChangeEntry>
{
    public void Configure(EntityTypeBuilder<ChangeEntry> builder)
    {
        builder.ToTable("Changes");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.EntityType).IsRequired().HasMaxLength(64);
        builder.Property(c => c.EntityId).IsRequired();
        builder.Property(c => c.Field).IsRequired().HasMaxLength(64);
        builder.Property(c => c.Value);
        builder.Property(c => c.Hlc).IsRequired().HasMaxLength(64);
        builder.Property(c => c.Sent).IsRequired();

        // Kolejka wysyłkowa czyta wyłącznie niewysłane, w kolejności zegara.
        builder.HasIndex(c => new { c.Sent, c.Hlc });
    }
}
