using Marshal.Domain.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Marshal.Infrastructure.Data.Configurations;

public sealed class SavedFilterConfiguration : IEntityTypeConfiguration<SavedFilter>
{
    public void Configure(EntityTypeBuilder<SavedFilter> builder)
    {
        builder.ToTable("SavedFilters");
        builder.HasKey(f => f.Id);

        builder.Property(f => f.Name).IsRequired().HasMaxLength(100);

        // Jedna kolumna na cały filtr — zob. FilterQuery. Bez ograniczenia długości:
        // warunek po tagach potrafi wyliczyć kilkanaście identyfikatorów.
        builder.Property(f => f.DefinitionJson).IsRequired();

        builder.Property(f => f.SortOrder).IsRequired();
        builder.Property(f => f.CreatedAt).IsRequired();
        builder.Property(f => f.UpdatedAt).IsRequired();
        builder.Property(f => f.Deleted).IsRequired();

        builder.HasIndex(f => f.SortOrder);
    }
}
