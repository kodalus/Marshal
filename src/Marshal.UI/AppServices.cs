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
            deviceId ?? DeviceId());

        services.AddSingleton<ClarifyViewModel>();
        services.AddSingleton<MainViewModel>();

        _provider = services.BuildServiceProvider();
        return _provider;
    }

    /// <summary>
    /// Identyfikator urządzenia rozstrzyga remisy zegara logicznego (spec 3.5), więc
    /// musi być trwały i różny na każdym urządzeniu. Nazwa maszyny wystarcza do etapu 3;
    /// przy synchronizacji dostanie własny, losowy identyfikator zapisany w ustawieniach.
    /// </summary>
    private static string DeviceId()
    {
        var name = Environment.MachineName;
        var clean = new string(name.Where(char.IsLetterOrDigit).ToArray());
        return string.IsNullOrEmpty(clean) ? "urzadzenie" : clean.ToLowerInvariant();
    }
}
