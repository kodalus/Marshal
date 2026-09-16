using Marshal.Infrastructure;
using Marshal.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Marshal.UI;

/// <summary>
/// Złożenie zależności i przygotowanie bazy. Wspólne dla Windowsa i Androida —
/// obie platformy różnią się tylko sposobem uruchomienia okna.
/// </summary>
public static class AppServices
{
    private static IServiceProvider? _provider;

    public static IServiceProvider Provider =>
        _provider ?? throw new InvalidOperationException("Zależności nie zostały jeszcze złożone.");

    public static IServiceProvider Build(string? databasePath = null, string? deviceId = null)
    {
        var services = new ServiceCollection();

        services.AddMarshal(
            databasePath ?? DependencyInjection.DefaultDatabasePath(),
            deviceId);

        services.AddSingleton<ClarifyViewModel>();
        services.AddSingleton<TaskDetailViewModel>();
        services.AddSingleton<MainViewModel>();

        _provider = services.BuildServiceProvider();
        return _provider;
    }
}
