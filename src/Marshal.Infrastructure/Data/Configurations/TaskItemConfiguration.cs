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
        builder.Property(t => t.RecurrenceJson).HasMaxLength(500);
        builder.Property(t => t.RollCount).IsRequired();
        builder.Property(t => t.Energy).IsRequired().HasConversion<int>();
        builder.Property(t => t.FocusMissCount).IsRequired();
        builder.Property(t => t.DoTime);
        builder.HasIndex(t => t.ReminderAt);

        // Wybór na dziś odpytywany jest przy każdym otwarciu „Dzisiaj" i przy każdym
        // przejściu dnia.
        builder.HasIndex(t => t.FocusDate);

        // Reguła jest w bazie jednym tekstem (RecurrenceJson); to tylko jej odczytana
        // postać. Zmapowana byłaby drugą, niespójną kopią tej samej rzeczy.
        builder.Ignore(t => t.Recurrence);

        // Liczone, nie przechowywane — inaczej dałoby się mieć HasDeadline bez Deadline.
        builder.Ignore(t => t.HasDeadline);

        builder.HasIndex(t => t.State);
        builder.HasIndex(t => t.AreaId);
        builder.HasIndex(t => t.ProjectId);
        builder.HasIndex(t => t.ParentTaskId);
        builder.HasIndex(t => t.DoDate);
        builder.HasIndex(t => t.Deadline);
        builder.HasIndex(t => t.Priority);

        // Przejście dnia odpytuje po tej dacie przy każdym starcie aplikacji.
        builder.HasIndex(t => new { t.State, t.DoDate });

        // Świadomie bez kluczy obcych do Areas i Projects — zob. uwagę
        // w MarshalDbContext.OnModelCreating.
    }
}
