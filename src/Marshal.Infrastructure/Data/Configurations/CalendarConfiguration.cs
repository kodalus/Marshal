using Marshal.Domain.Calendar;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Marshal.Infrastructure.Data.Configurations;

public sealed class CalendarSourceConfiguration : IEntityTypeConfiguration<CalendarSource>
{
    public void Configure(EntityTypeBuilder<CalendarSource> builder)
    {
        builder.ToTable("CalendarSources");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Kind).IsRequired().HasConversion<int>();

        // Adres kanału iCal bywa długi; identyfikator kalendarza Google to adres pocztowy.
        builder.Property(s => s.ExternalId).IsRequired().HasMaxLength(500);
        builder.Property(s => s.Name).IsRequired().HasMaxLength(200);
        builder.Property(s => s.Color).HasMaxLength(16);
        builder.Property(s => s.IsVisible).IsRequired();

        // Adres pocztowy konta, z którego pochodzi kalendarz. Puste = konto główne.
        builder.Property(s => s.Account).HasMaxLength(320);

        // Poziom dostępu z Google. Lokalny w znaczeniu „odpowiedź na pytanie, czy mi
        // wolno", a nie decyzja o danych — jedzie jednak zwykłą drogą, bo to kolumna
        // encji i wyjmowanie jej z dziennika zmian byłoby osobnym wyjątkiem do pilnowania.
        builder.Property(s => s.ReadOnly).IsRequired();
        builder.Property(s => s.CreatedAt).IsRequired();
        builder.Property(s => s.UpdatedAt).IsRequired();
        builder.Property(s => s.Deleted).IsRequired();
    }
}

public sealed class CalendarEventConfiguration : IEntityTypeConfiguration<CalendarEvent>
{
    public void Configure(EntityTypeBuilder<CalendarEvent> builder)
    {
        builder.ToTable("CalendarEvents");

        // Klucz naturalny drugiej strony. Własny identyfikator byłby trzecim numerem
        // tego samego wydarzenia, a szukać i tak trzeba by po tej parze.
        builder.HasKey(e => new { e.SourceId, e.ExternalId });

        builder.Property(e => e.ExternalId).HasMaxLength(500);
        builder.Property(e => e.Title).IsRequired().HasMaxLength(500);
        builder.Property(e => e.StartsAt).IsRequired();
        builder.Property(e => e.EndsAt).IsRequired();
        builder.Property(e => e.IsAllDay).IsRequired();
        builder.Property(e => e.Location).HasMaxLength(500);
        builder.Property(e => e.Cancelled).IsRequired();

        // Siatka pyta o zakres dni przy każdym przewinięciu.
        builder.HasIndex(e => e.StartsAt);
    }
}

public sealed class CalendarCursorConfiguration : IEntityTypeConfiguration<CalendarCursor>
{
    public void Configure(EntityTypeBuilder<CalendarCursor> builder)
    {
        builder.ToTable("CalendarCursors");
        builder.HasKey(c => c.SourceId);

        builder.Property(c => c.SyncToken).HasMaxLength(500);
        builder.Property(c => c.FetchedAt).IsRequired();
    }
}
