namespace Marshal.UI;

/// <summary>
/// Zejście aplikacji w tło i powrót na wierzch.
/// </summary>
/// <remarks>
/// <para>
/// Haczyk w warstwie współdzielonej, w tej samej postaci co <see cref="Wstecz"/>:
/// okno wie, <b>co</b> ma przestać robić, a nie wie, kiedy — to wie system. Projekt
/// Androida podstawia tu wywołania z cyklu życia okna, pulpit zostawia pusto, bo tam
/// aplikacja niewidoczna dalej jest aplikacją uruchomioną i nic jej nie zamraża.
/// </para>
/// <para>
/// <b>Po co to w ogóle jest.</b> Minutnik okna robi co minutę rzeczy, które sięgają
/// do bazy i do sieci: przejście dnia, przypomnienia, zaległe odbicia, pobranie
/// kalendarzy, synchronizacja. W tle to jest praca podwójnie zła. Po pierwsze zbędna:
/// od roboty w tle są <c>SynchronizacjaWorker</c> i <c>Budzik</c>, czyli mechanizmy,
/// którym system na nią pozwala. Po drugie ryzykowna: Android trzyma schowaną
/// aplikację jako <i>zamrożoną</i> i potrafi ją zatrzymać w środku wywołania
/// natywnego — a wątek zamrożony w połowie zapytania do bazy albo w połowie odczytu
/// z sieci jest dokładnie tym, po czym środowisko uruchomieniowe przy odmrożeniu nie
/// umie się pozbierać. Trzecia rzecz jest najbardziej przyziemna: co minuta pracy
/// w tle to bateria i to powód, dla którego producenckie zarządzanie energią uznaje
/// aplikację za taką, którą warto ubić.
/// </para>
/// </remarks>
public static class Sleep
{
    /// <summary>Okno schodzi w tło. Puste, dopóki okna nie ma.</summary>
    public static Action? OnSleep { get; set; }

    /// <summary>Okno wraca na wierzch.</summary>
    public static Action? OnWake { get; set; }

    public static void Enter() => OnSleep?.Invoke();

    public static void Leave() => OnWake?.Invoke();
}
