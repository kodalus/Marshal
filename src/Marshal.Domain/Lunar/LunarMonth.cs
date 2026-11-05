namespace Marshal.Domain.Lunar;

/// <summary>
/// Jeden miesiąc księżycowy: od dnia nowiu do dnia przed następnym nowiem.
/// </summary>
/// <param name="Year">Rok księżycowy, do którego miesiąc należy.</param>
/// <param name="Number">Numer miesiąca w roku — od 1 do 12 albo 13.</param>
/// <param name="Starts">Dzień nowiu, czyli pierwszy dzień miesiąca.</param>
/// <param name="Ends">Dzień przed następnym nowiem, czyli ostatni dzień miesiąca.</param>
/// <param name="FullMoon">Dzień pełni, jeśli wypada w tym miesiącu.</param>
public sealed record LunarMonth(
    int Year,
    int Number,
    DateOnly Starts,
    DateOnly Ends,
    DateOnly? FullMoon)
{
    /// <summary>Długość miesiąca w dniach — zawsze 29 albo 30.</summary>
    public int Length => Ends.DayNumber - Starts.DayNumber + 1;

    /// <summary>
    /// Czy to miesiąc trzynasty, czyli ten, którego w większości lat nie ma.
    /// </summary>
    /// <remarks>
    /// Dwanaście miesięcy księżycowych to 354 dni, rok słoneczny ma 365 — raz na dwa
    /// albo trzy lata przesilenie wypada tak, że między dwoma kolejnymi mieści się
    /// trzynaście nowiów. Trzynasty jest wtedy zawsze ostatni, bo rok kończy się tam,
    /// gdzie wyznaczy go następne przesilenie, a nie tam, gdzie wypadnie dwunasty
    /// miesiąc.
    /// </remarks>
    public bool IsThirteenth => Number == 13;
}
