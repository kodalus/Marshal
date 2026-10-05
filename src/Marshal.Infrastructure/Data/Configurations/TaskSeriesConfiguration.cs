using Marshal.Domain.Series;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Marshal.Infrastructure.Data.Configurations;

public sealed class TaskSeriesConfiguration : IEntityTypeConfiguration<TaskSeries>
{
    public void Configure(EntityTypeBuilder<TaskSeries> builder)
    {
        builder.ToTable("TaskSeries");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Starts).IsRequired();

        // Bez limitu długości: reguła i szablon są tekstem JSON, a szablon niesie
        // notatkę. Limit znaczyłby notatkę uciętą w połowie zdania przy zapisie,
        // którego nikt nie sprawdza, bo to pole służebne.
        builder.Property(s => s.RuleJson).IsRequired();
        builder.Property(s => s.TemplateJson).IsRequired();

        builder.Property(s => s.CreatedAt).IsRequired();
        builder.Property(s => s.UpdatedAt).IsRequired();
        builder.Property(s => s.Deleted).IsRequired();
    }
}
