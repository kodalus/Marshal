using System.Reflection;
using Marshal.Domain.Areas;
using Marshal.Domain.Primitives;
using Marshal.Domain.Projects;
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
