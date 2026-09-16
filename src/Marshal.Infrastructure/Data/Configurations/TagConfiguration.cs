using Marshal.Domain.Tags;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Marshal.Infrastructure.Data.Configurations;

public sealed class TagConfiguration : IEntityTypeConfiguration<Tag>
{
    public void Configure(EntityTypeBuilder<Tag> builder)
    {
        builder.ToTable("Tags");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Name).IsRequired().HasMaxLength(100);
        builder.Property(t => t.Color).HasMaxLength(16);
        builder.Property(t => t.SortOrder).IsRequired();
        builder.Property(t => t.CreatedAt).IsRequired();
        builder.Property(t => t.UpdatedAt).IsRequired();
        builder.Property(t => t.Deleted).IsRequired();

        // Bez indeksu jednoznacznego na nazwie: przy synchronizacji dwa urządzenia
        // mogą offline utworzyć ten sam tag, a odrzucenie drugiego przy scalaniu
        // byłoby utratą zmiany. Powtórki scala się w warstwie aplikacji.
        builder.HasIndex(t => t.Name);
    }
}
