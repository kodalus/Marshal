namespace Marshal.Domain.Filters;

/// <summary>
/// Okno czasowe warunku daty — **względne, nigdy bezwzględne** (spec 11.5).
/// </summary>
/// <remarks>
/// <para>
/// To jest cała różnica między filtrem zapisanym a filtrem jednorazowym. Warunek
/// „termin przed 20 września" zapisany do Ulubionych jest poprawny przez cztery dni,
/// a potem po cichu przestaje cokolwiek znaczyć: nadal się uruchamia, nadal coś zwraca
/// i nadal wygląda na działający. Zapisany widok musi opisywać **położenie względem
/// dzisiaj**, bo tylko takie zdanie jest prawdziwe również za miesiąc.
/// </para>
/// <para>
/// Stąd brak wybieraka daty w konstruktorze warunków. Nie jest to uproszczenie do
/// nadrobienia później — data bezwzględna w zapisanym widoku jest po prostu błędem.
/// </para>
/// </remarks>
public enum DateWindow
{
    /// <summary>Pole ustawione, obojętnie na kiedy.</summary>
    Any,

    /// <summary>Pole puste. „Następne akcje bez wyznaczonego dnia" to właśnie to.</summary>
    None,

    /// <summary>Wcześniej niż dziś.</summary>
    Overdue,

    /// <summary>Dzisiaj.</summary>
    Today,

    /// <summary>
    /// Dziś i sześć następnych dni.
    /// </summary>
    /// <remarks>
    /// Tydzień licząc od dzisiaj, a nie tydzień kalendarzowy. Tydzień kalendarzowy
    /// kurczy się z każdym dniem i w sobotę pokazuje jeden dzień albo zero — czyli
    /// filtr „na ten tydzień" byłby najbardziej pusty wtedy, kiedy się go otwiera
    /// przy planowaniu weekendu.
    /// </remarks>
    ThisWeek,

    /// <summary>Dziś i trzydzieści następnych dni.</summary>
    Next30Days,

    /// <summary>Później niż dziś.</summary>
    Future,
}
