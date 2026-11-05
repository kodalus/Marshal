namespace Marshal.Domain.Lunar;

/// <summary>
/// Data księżycowa jednego dnia kalendarza gregoriańskiego.
/// </summary>
/// <param name="Year">Rok księżycowy.</param>
/// <param name="Month">Numer miesiąca — od 1 do 12 albo 13. Nazwy są warstwą wyświetlania.</param>
/// <param name="Day">Dzień miesiąca, licząc od 1 w dniu nowiu.</param>
/// <param name="Length">Długość tego miesiąca w dniach: 29 albo 30.</param>
/// <param name="IsNewMoon">Czy to dzień nowiu, czyli pierwszy dzień miesiąca.</param>
/// <param name="IsFullMoon">Czy to dzień pełni — zwykle czternasty albo piętnasty.</param>
/// <param name="IsThirteenth">Czy to miesiąc trzynasty, dodatkowy.</param>
/// <remarks>
/// Typ niesie <b>liczby, nie nazwy</b>. Nazwy miesięcy są ustawieniem urządzenia
/// i mogą być wyłączone albo zmienione, a rachunek nie może od nich zależeć — inaczej
/// zmiana nazwy w ustawieniach zmieniałaby kalendarz, a nie jego podpis.
/// </remarks>
public readonly record struct LunarDate(
    int Year,
    int Month,
    int Day,
    int Length,
    bool IsNewMoon,
    bool IsFullMoon,
    bool IsThirteenth);
