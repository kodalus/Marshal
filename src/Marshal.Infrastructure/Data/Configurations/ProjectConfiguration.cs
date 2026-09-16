using Marshal.Domain.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Marshal.Infrastructure.Data.Configurations;

public sealed class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.ToTable("Projects");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Outcome).IsRequired().HasMaxLength(500);
        builder.Property(p => p.Note);
        builder.Property(p => p.State).IsRequired().HasConversion<int>();
        builder.Property(p => p.AreaId).IsRequired();
        builder.Property(p => p.SortOrder).IsRequired();
        builder.Property(p => p.CreatedAt).IsRequired();
        builder.Property(p => p.UpdatedAt).IsRequired();
        builder.Property(p => p.Deleted).IsRequired();

        builder.HasIndex(p => p.AreaId);
        builder.HasIndex(p => p.ParentProjectId);
        builder.HasIndex(p => p.State);
    }
}
