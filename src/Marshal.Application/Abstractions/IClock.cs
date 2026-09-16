namespace Marshal.Application.Abstractions;

/// <summary>Czas fizyczny. Wydzielony, żeby testy nie zależały od zegara maszyny.</summary>
public interface IClock
{
    /// <summary>Chwila w strefie z ustawień (spec 3.4), nie w strefie systemu.</summary>
    DateTimeOffset Now { get; }

    /// <summary>
    /// Dzisiejszy dzień, liczony w tej samej strefie co <see cref="Now"/>.
    /// </summary>
    /// <remarks>
    /// Jedno miejsce zamiast <c>DateOnly.FromDateTime(clock.Now.???)</c> rozsianego po
    /// kilkunastu plikach. Rozsiane było groźne nie dlatego, że długie, tylko dlatego,
    /// że każde z tych miejsc mogło wybrać inną z trzech właściwości —
    /// <c>DateTime</c>, <c>LocalDateTime</c>, <c>UtcDateTime</c> — a różnią się
    /// dokładnie o tyle, żeby raz na dobę dać inny dzień.
    /// </remarks>
    DateOnly Today => DateOnly.FromDateTime(Now.DateTime);
}
