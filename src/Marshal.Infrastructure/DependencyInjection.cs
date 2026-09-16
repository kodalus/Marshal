using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Application.UseCases;
using Marshal.Infrastructure.Data;
using Marshal.Infrastructure.Repositories;
using Marshal.Infrastructure.Sync;
using Marshal.Infrastructure.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Marshal.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Ścieżka bazy. Ten sam kod na Windowsie i na Androidzie: na obu platformach
    /// <see cref="Environment.SpecialFolder.LocalApplicationData"/> wskazuje prywatny
    /// katalog aplikacji.
    /// </summary>
    public static string DefaultDatabasePath()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Marshal");

        Directory.CreateDirectory(folder);
        return Path.Combine(folder, "marshal.db");
    }

    /// <param name="deviceId">
    /// Wymuszony identyfikator urządzenia. Tylko do testów — normalnie bierze się
    /// go z bazy, żeby był trwały i różny na każdym urządzeniu.
    /// </param>
    public static IServiceCollection AddMarshal(
        this IServiceCollection services, string databasePath, string? deviceId = null)
    {
        // Jeden kontekst na całą aplikację. Przy jednym użytkowniku i pracy
        // wyłącznie lokalnej to najprostsze rozwiązanie, które działa. Do ponownego
        // rozważenia w etapie 3: scalanie synchronizacji będzie chciało własnego
        // kontekstu, żeby nie mieszać śledzenia zmian z tym, co widzi ekran.
        services.AddDbContext<MarshalDbContext>(
            options => options
                .UseSqlite($"Data Source={databasePath}")
                .AddInterceptors(new ChangeJournalInterceptor()),
            ServiceLifetime.Singleton,
            ServiceLifetime.Singleton);

        services.AddSingleton<IClock, SystemClock>();

        services.AddSingleton<IDeviceIdentity>(sp => deviceId is null
            ? new DeviceIdentity(sp.GetRequiredService<MarshalDbContext>())
            : new FixedDeviceIdentity(deviceId));

        // Zegar wznawiany z bazy. Rozstrzygnięcie leniwe, bo identyfikator urządzenia
        // i zapisany znacznik leżą w bazie, a ta jest gotowa dopiero po PrepareAsync.
        services.AddSingleton<IHlcSource>(sp =>
        {
            var db = sp.GetRequiredService<MarshalDbContext>();
            var id = sp.GetRequiredService<IDeviceIdentity>().Id;

            return new HlcSource(sp.GetRequiredService<IClock>(), id, LastHlcStore.Read(db, id));
        });

        services.AddSingleton<ITaskRepository, TaskRepository>();
        services.AddSingleton<IProjectRepository, ProjectRepository>();
        services.AddSingleton<IAreaRepository, AreaRepository>();
        services.AddSingleton<ITagRepository, TagRepository>();
        services.AddSingleton<IUnitOfWork, UnitOfWork>();

        services.AddSingleton<InboxService>();
        services.AddSingleton<TagService>();

        return services;
    }

    /// <summary>Migracje i obszary początkowe. Wołane raz, przy starcie.</summary>
    public static async Task PrepareAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var db = services.GetRequiredService<MarshalDbContext>();
        await db.Database.MigrateAsync(ct);

        // Identyfikator urządzenia rozstrzygany zaraz po migracji: zakłada go przy
        // pierwszym uruchomieniu, a zegar logiczny potrzebuje go do wznowienia.
        _ = services.GetRequiredService<IDeviceIdentity>().Id;

        await AreaSeed.EnsureAsync(
            db,
            services.GetRequiredService<IClock>(),
            services.GetRequiredService<IHlcSource>(),
            ct);
    }
}

/// <summary>Identyfikator podany z zewnątrz — do testów i do scenariuszy z dwoma bazami.</summary>
internal sealed class FixedDeviceIdentity(string id) : IDeviceIdentity
{
    public string Id { get; } = id;
}
