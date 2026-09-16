using Marshal.Domain.Areas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Marshal.Infrastructure.Data.Configurations;

public sealed class AreaConfiguration : IEntityTypeConfiguration<Area>
{
    public void Configure(EntityTypeBuilder<Area> builder)
    {
        builder.ToTable("Areas");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.Name).IsRequired().HasMaxLength(100);
        builder.Property(a => a.Color).HasMaxLength(16);
        builder.Property(a => a.SortOrder).IsRequired();
        builder.Property(a => a.IsActive).IsRequired();
        builder.Property(a => a.QuietDays).IsRequired();
        builder.Property(a => a.DefaultNudgeDays).IsRequired();
        builder.Property(a => a.CreatedAt).IsRequired();
        builder.Property(a => a.UpdatedAt).IsRequired();
        builder.Property(a => a.Deleted).IsRequired();

        builder.HasIndex(a => a.SortOrder);

        // Nagrobki zostają w tabeli (spec 5.1) — nie filtrujemy ich zapytaniem
        // globalnym, bo synchronizacja musi je widzieć.
        builder.HasIndex(a => a.Deleted);
    }
}
