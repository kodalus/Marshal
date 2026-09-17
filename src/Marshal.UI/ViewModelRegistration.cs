using Marshal.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Marshal.UI;

/// <summary>
/// Rejestracja modeli widoków — wydzielona, żeby dało się ją złożyć w teście.
/// </summary>
/// <remarks>
/// Siedziała w <see cref="AppServices"/> razem ze statycznym dostawcą, więc jedynym
/// sposobem jej sprawdzenia było uruchomienie aplikacji. 17.09 wyszło, po co: dwa
/// błędy składania naraz, oba widoczne dopiero jako białe tło i zamknięcie okna.
/// Osobna metoda znaczy, że test może złożyć **dokładnie ten sam graf**, co okno,
/// nie zaglądając w statyczny stan.
/// </remarks>
public static class ViewModelRegistration
{
    public static IServiceCollection AddMarshalViewModels(this IServiceCollection services)
    {
        services.AddSingleton<ClarifyViewModel>();
        services.AddSingleton<TaskDetailViewModel>();
        services.AddSingleton<ReviewViewModel>();
        services.AddSingleton<NowViewModel>();
        services.AddSingleton<CalendarViewModel>();
        services.AddSingleton<NotesViewModel>();
        services.AddSingleton<FiltersViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainViewModel>();

        return services;
    }
}
