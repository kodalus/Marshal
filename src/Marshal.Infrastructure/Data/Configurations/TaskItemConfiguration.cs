using Marshal.Domain.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Marshal.Infrastructure.Data.Configurations;

public sealed class TaskItemConfiguration : IEntityTypeConfiguration<TaskItem>
{
    public void Configure(EntityTypeBuilder<TaskItem> builder)
    {
        builder.ToTable("Tasks");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Title).IsRequired().HasMaxLength(500);
        builder.Property(t => t.Note);
        builder.Property(t => t.State).IsRequired().HasConversion<int>();
        builder.Property(t => t.Priority).IsRequired().HasConversion<int>();
        builder.Property(t => t.Color).HasMaxLength(16);
        builder.Property(t => t.WaitingForWho).HasMaxLength(200);
        builder.Property(t => t.SortOrder).IsRequired();
        builder.Property(t => t.CreatedAt).IsRequired();
        builder.Property(t => t.UpdatedAt).IsRequired();
        builder.Property(t => t.Deleted).IsRequired();

        builder.HasIndex(t => t.State);
        builder.HasIndex(t => t.AreaId);
        builder.HasIndex(t => t.ProjectId);
        builder.HasIndex(t => t.ParentTaskId);
        builder.HasIndex(t => t.DoDate);
        builder.HasIndex(t => t.Deadline);
        builder.HasIndex(t => t.Priority);

        // Świadomie bez kluczy obcych do Areas i Projects — zob. uwagę
        // w MarshalDbContext.OnModelCreating.
    }
}
