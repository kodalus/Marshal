using Marshal.Infrastructure;
using Marshal.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Marshal.UI;

/// <summary>
/// Złożenie zależności i przygotowanie bazy. Wspólne dla Windowsa i Androida —
/// obie platformy różnią się tylko sposobem uruchomienia okna.
/// </summary>
/// <remarks>
/// Składanie jest **jednorazowe na proces**. Nie jest to wygoda: kontekst bazy jest
/// pojedynczy, więc drugie złożenie dałoby dwa konteksty nad tym samym plikiem,
/// każdy z własnym śledzeniem zmian — a wtedy zapis z jednej strony byłby dla drugiej
/// niewidoczny aż do ponownego odczytu. Na Androidzie ma to znaczenie wprost:
/// widget ekranu domowego wchodzi tą samą drogą co okno i bywa pierwszy.
/// </remarks>
public static class AppServices
{
    private static readonly Lock Gate = new();

    private static IServiceProvider? _provider;

    private static Task? _ready;

    public static IServiceProvider Provider =>
        _provider ?? throw new InvalidOperationException("Zależności nie zostały jeszcze złożone.");

    /// <param name="databasePath">
    /// Ścieżka bazy. **Brana pod uwagę wyłącznie przy pierwszym wywołaniu** — drugie
    /// oddaje to, co już złożone, i ten argument pomija. Inaczej byłby to wybór między
    /// dwoma kontekstami nad dwoma plikami a po cichu zignorowanym żądaniem; drugie
    /// jest złe, ale pierwsze jest gorsze.
    /// </param>
    /// <param name="deviceId">Jak wyżej. Wymuszany tylko w testach.</param>
    public static IServiceProvider Build(string? databasePath = null, string? deviceId = null)
    {
        lock (Gate)
        {
            if (_provider is not null)
            {
                return _provider;
            }

            var services = new ServiceCollection();

            services.AddMarshal(
                databasePath ?? DependencyInjection.DefaultDatabasePath(),
                deviceId);

            services.AddSingleton<ClarifyViewModel>();
            services.AddSingleton<TaskDetailViewModel>();
            services.AddSingleton<ReviewViewModel>();
            services.AddSingleton<NowViewModel>();
            services.AddSingleton<CalendarViewModel>();
            services.AddSingleton<NotesViewModel>();
            services.AddSingleton<FiltersViewModel>();
            services.AddSingleton<SettingsViewModel>();
            services.AddSingleton<MainViewModel>();

            return _provider = services.BuildServiceProvider();
        }
    }

    /// <summary>
    /// Zależności złożone **i baza gotowa** — migracje, obszary początkowe, przejście
    /// dnia. Raz na proces, choćby wołane z kilku miejsc naraz.
    /// </summary>
    /// <remarks>
    /// Zadanie zapamiętane, a nie flaga „zrobione": okno i widget potrafią wejść tu
    /// w tej samej chwili, a flaga przepuściłaby drugiego przed końcem migracji.
    /// Czekanie na to samo zadanie znaczy, że drugi po prostu poczeka.
    /// </remarks>
    public static Task ReadyAsync()
    {
        lock (Gate)
        {
            return _ready ??= DependencyInjection.PrepareAsync(Build());
        }
    }
}
