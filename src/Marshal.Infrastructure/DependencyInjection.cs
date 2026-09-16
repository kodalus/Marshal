using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Application.UseCases;
using Marshal.Infrastructure.Data;
using Marshal.Infrastructure.Repositories;
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

    public static IServiceCollection AddMarshal(
        this IServiceCollection services, string databasePath, string deviceId)
    {
        // Jeden kontekst na całą aplikację. Przy jednym użytkowniku i pracy
        // wyłącznie lokalnej to najprostsze rozwiązanie, które działa. Do ponownego
        // rozważenia w etapie 3: scalanie synchronizacji będzie chciało własnego
        // kontekstu, żeby nie mieszać śledzenia zmian z tym, co widzi ekran.
        services.AddDbContext<MarshalDbContext>(
            options => options.UseSqlite($"Data Source={databasePath}"),
            ServiceLifetime.Singleton,
            ServiceLifetime.Singleton);

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IHlcSource>(sp => new HlcSource(sp.GetRequiredService<IClock>(), deviceId));

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

        await AreaSeed.EnsureAsync(
            db,
            services.GetRequiredService<IClock>(),
            services.GetRequiredService<IHlcSource>(),
            ct);
    }
}
