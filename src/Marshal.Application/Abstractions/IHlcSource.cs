using Marshal.Domain.Primitives;

namespace Marshal.Application.Abstractions;

/// <summary>
/// Źródło znaczników zmian dla tego urządzenia. Stanowe i współbieżne — każde
/// wywołanie zwraca znacznik ściśle większy od poprzedniego.
/// </summary>
public interface IHlcSource
{
    string DeviceId { get; }

    /// <summary>
    /// Ostatni wydany znacznik. Musi przetrwać zamknięcie aplikacji, inaczej zegar
    /// wystartuje od zera i zależy wyłącznie od zegara ściennego.
    /// </summary>
    Hlc Last { get; }

    /// <summary>Znacznik dla zmiany lokalnej.</summary>
    Hlc Next();

    /// <summary>
    /// Podnosi zegar lokalny po odebraniu zmiany z innego urządzenia, żeby kolejna
    /// zmiana lokalna była późniejsza od wszystkiego, co widziano (spec 9.4).
    /// </summary>
    Hlc Observe(Hlc remote);
}
