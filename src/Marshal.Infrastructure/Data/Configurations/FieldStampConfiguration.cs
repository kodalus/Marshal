using Marshal.Domain.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Marshal.Infrastructure.Data.Configurations;

public sealed class FieldStampConfiguration : IEntityTypeConfiguration<FieldStamp>
{
    public void Configure(EntityTypeBuilder<FieldStamp> builder)
    {
        builder.ToTable("FieldStamps");

        // Klucz złożony zamiast sztucznego: wiersz jest jednoznacznie wyznaczony
        // przez pole encji, a wyszukiwanie po tej trójce to jedyna operacja,
        // jaką na tej tabeli wykonujemy.
        builder.HasKey(f => new { f.EntityType, f.EntityId, f.Field });

        builder.Property(f => f.EntityType).HasMaxLength(64);
        builder.Property(f => f.Field).HasMaxLength(64);
        builder.Property(f => f.Hlc).IsRequired().HasMaxLength(64);
    }
}
