using System.Reflection;
using Marshal.Domain.Areas;
using Marshal.Domain.Attachments;
using Marshal.Domain.Calendar;
using Marshal.Domain.Diagnostics;
using Marshal.Domain.Filters;
using Marshal.Domain.Notes;
using Marshal.Domain.Primitives;
using Marshal.Domain.Projects;
using Marshal.Domain.Review;
using Marshal.Domain.Sync;
using Marshal.Domain.Tags;
using Marshal.Domain.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Marshal.Infrastructure.Data;

public sealed class MarshalDbContext : DbContext
{
    public MarshalDbContext(DbContextOptions<MarshalDbContext> options)
        : base(options)
    {
    }

    public DbSet<Area> Areas => Set<Area>();

    public DbSet<Project> Projects => Set<Project>();

    public DbSet<TaskItem> Tasks => Set<TaskItem>();

    public DbSet<Tag> Tags => Set<Tag>();

    public DbSet<TaskTag> TaskTags => Set<TaskTag>();

    /// <summary>Dziennik zmian — lokalny, niesynchronizowany. To on jest tym, co się wysyła.</summary>
    public DbSet<ChangeEntry> Changes => Set<ChangeEntry>();

    /// <summary>Znaczniki zegara dla bieżących wartości pól. Warunek scalania per pole.</summary>
    public DbSet<FieldStamp> FieldStamps => Set<FieldStamp>();

    /// <summary>Dokąd doczytaliśmy plik każdego z pozostałych urządzeń.</summary>
    public DbSet<SyncCursor> SyncCursors => Set<SyncCursor>();

    /// <summary>Ustawienia tego urządzenia — identyfikator, ostatni znacznik zegara.</summary>
    public DbSet<LocalSetting> LocalSettings => Set<LocalSetting>();

    /// <summary>Przypomnienia pokazane przez to urządzenie. Lokalne, niesynchronizowane.</summary>
    public DbSet<ReminderShown> ReminderShown => Set<ReminderShown>();

    /// <summary>Przeglądy tygodniowe — także te w trakcie. Synchronizowane.</summary>
    public DbSet<ReviewSession> ReviewSessions => Set<ReviewSession>();

    /// <summary>Materiał referencyjny. Przypięte wchodzą do kroku zerowego przeglądu.</summary>
    public DbSet<Note> Notes => Set<Note>();

    /// <summary>Wpisy załączników. Treść plików wędruje osobną drogą.</summary>
    public DbSet<Attachment> Attachments => Set<Attachment>();

    /// <summary>Zapisane widoki — Ulubione. Decyzja, więc synchronizowane.</summary>
    public DbSet<SavedFilter> SavedFilters => Set<SavedFilter>();

    /// <summary>Które kalendarze pokazywać i w jakim kolorze. Decyzja, więc synchronizowana.</summary>
    public DbSet<CalendarSource> CalendarSources => Set<CalendarSource>();

    /// <summary>Kopia wydarzeń z kalendarzy zewnętrznych. Lokalna — każde urządzenie pobiera sobie samo.</summary>
    public DbSet<CalendarEvent> CalendarEvents => Set<CalendarEvent>();

    /// <summary>Żetony odczytu przyrostowego. Lokalne — żeton należy do urządzenia, które go dostało.</summary>
    public DbSet<CalendarCursor> CalendarCursors => Set<CalendarCursor>();

    /// <summary>Co aplikacja zrobiła i co z tego wyszło. Lokalne — opisuje to urządzenie.</summary>
    public DbSet<ActivityEntry> ActivityEntries => Set<ActivityEntry>();

    /// <remarks>
    /// Powiązania między agregatami są trzymane jako gołe identyfikatory, **bez kluczy
    /// obcych**. Nie jest to niedopatrzenie: przy synchronizacji plikowej (spec 9) zmiany
    /// z drugiego urządzenia przychodzą w kolejności zapisu, nie w kolejności zależności,
    /// więc zadanie potrafi dotrzeć przed swoim projektem. Klucz obcy odrzuciłby wtedy
    /// poprawną zmianę i rozjechał obie bazy na trwałe. Spójność pilnują niezmienniki
    /// z rozdziału 6, sprawdzane po scaleniu.
    /// </remarks>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
        base.OnModelCreating(modelBuilder);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<Hlc>()
            .HaveConversion<HlcConverter>()
            .HaveMaxLength(64);

        // SQLite nie porządkuje typu DateTimeOffset — zob. DateTimeOffsetConverter.
        configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<DateTimeOffsetConverter>();

        // GUID jako tekst, nie blob: baza daje się czytać narzędziami, a postać
        // jest ta sama co w logu synchronizacji.
        configurationBuilder.Properties<Guid>()
            .HaveConversion<GuidToStringConverter>()
            .HaveMaxLength(36);

        base.ConfigureConventions(configurationBuilder);
    }
}
