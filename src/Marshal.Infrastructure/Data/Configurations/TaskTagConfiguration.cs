using Marshal.Domain.Tags;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Marshal.Infrastructure.Data.Configurations;

public sealed class TaskTagConfiguration : IEntityTypeConfiguration<TaskTag>
{
    public void Configure(EntityTypeBuilder<TaskTag> builder)
    {
        builder.ToTable("TaskTags");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.TaskId).IsRequired();
        builder.Property(t => t.TagId).IsRequired();
        builder.Property(t => t.CreatedAt).IsRequired();
        builder.Property(t => t.UpdatedAt).IsRequired();
        builder.Property(t => t.Deleted).IsRequired();

        builder.HasIndex(t => t.TaskId);
        builder.HasIndex(t => t.TagId);
    }
}
