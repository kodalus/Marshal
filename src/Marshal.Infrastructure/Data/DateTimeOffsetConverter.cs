using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Marshal.Infrastructure.Data;

/// <summary>
/// Moment w czasie jako liczba taktów UTC.
/// </summary>
/// <remarks>
/// SQLite nie ma typu daty. Dostawca składuje <see cref="DateTimeOffset"/> jako tekst
/// i — zgodnie z dokumentacją EF Core — **nie potrafi takiej kolumny porównywać ani
/// porządkować**; próba sortowania kończy się wyjątkiem przy tłumaczeniu zapytania.
/// To ta sama pułapka co <c>decimal</c>, obchodzona w Castellanie przez trzymanie
/// pieniędzy w groszach jako <c>long</c>.
///
/// Zapis w taktach UTC daje porządek, jednoznaczność i stałą szerokość. Odczyt wraca
/// w czasie UTC — przesunięcie strefowe nie jest przechowywane, bo do niczego nie jest
/// potrzebne: strefa do wyświetlania jest zapisana w ustawieniach (spec 3.4), a granice
/// dni liczymy na typie <see cref="DateOnly"/>, który stref nie ma.
/// </remarks>
public sealed class DateTimeOffsetConverter : ValueConverter<DateTimeOffset, long>
{
    public DateTimeOffsetConverter()
        : base(moment => moment.UtcTicks, ticks => new DateTimeOffset(ticks, TimeSpan.Zero))
    {
    }
}
