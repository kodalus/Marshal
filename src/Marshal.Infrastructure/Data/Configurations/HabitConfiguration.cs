using Marshal.Domain.Habits;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Marshal.Infrastructure.Data.Configurations;

public sealed class HabitConfiguration : IEntityTypeConfiguration<Habit>
{
    public void Configure(EntityTypeBuilder<Habit> builder)
    {
        builder.ToTable("Habits");
        builder.HasKey(h => h.Id);

        builder.Property(h => h.Title).IsRequired().HasMaxLength(200);
        builder.Property(h => h.Color).HasMaxLength(9);
        builder.Property(h => h.Unit).HasMaxLength(40);
        builder.Property(h => h.SortOrder).IsRequired();
        builder.Property(h => h.Archived).IsRequired();
        builder.Property(h => h.CreatedAt).IsRequired();
        builder.Property(h => h.UpdatedAt).IsRequired();
        builder.Property(h => h.Deleted).IsRequired();

        // Lista pyta o żywe i nieodłożone przy każdym otwarciu ekranu.
        builder.HasIndex(h => h.Archived);
    }
}

public sealed class HabitMarkConfiguration : IEntityTypeConfiguration<HabitMark>
{
    public void Configure(EntityTypeBuilder<HabitMark> builder)
    {
        builder.ToTable("HabitMarks");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.HabitId).IsRequired();
        builder.Property(m => m.Day).IsRequired();
        builder.Property(m => m.Amount).IsRequired();
        builder.Property(m => m.CreatedAt).IsRequired();
        builder.Property(m => m.UpdatedAt).IsRequired();
        builder.Property(m => m.Deleted).IsRequired();

        // Siatka pyta o jeden nawyk i zakres dni — to jest dokładnie ten indeks.
        // Bez niego rok historii sześciu nawyków przeglądałby się po kolei przy
        // każdym przerysowaniu listy.
        builder.HasIndex(m => new { m.HabitId, m.Day });
    }
}
