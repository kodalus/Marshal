namespace Marshal.Domain.Recurrence;

/// <summary>
/// Wyliczanie kolejnego wystąpienia (spec 8.4, krok 3–4).
/// </summary>
/// <remarks>
/// <para>
/// Funkcja czysta: dostaje regułę i datę bazową, oddaje datę albo <c>null</c>, gdy
/// seria się skończyła. Nie dotyka bazy ani zegara, więc wszystkie przypadki brzegowe —
/// 31. dnia w miesiącu trzydziestodniowym, 29 lutego, koniec serii — dają się sprawdzić
/// wprost, bez stawiania zadania.
/// </para>
/// <para>
/// Wszystko liczone na <see cref="DateOnly"/>. To nie jest uproszczenie, tylko
/// rozstrzygnięcie: **zmiana czasu nie może przesunąć dnia**. Gdyby rytm liczyć na
/// chwilach, „co poniedziałek" po przejściu na czas zimowy potrafiłoby wypaść
/// w niedzielę o 23:00 — przypadek brzegowy, który przy tej reprezentacji nie istnieje.
/// </para>
/// </remarks>
public static class RecurrenceSchedule
{
    /// <summary>
    /// Pierwsza data **ściśle późniejsza** od <paramref name="from"/> zgodna z regułą,
    /// albo <c>null</c>, gdy seria jest wyczerpana przez <c>Count</c> lub <c>Until</c>.
    /// </summary>
    public static DateOnly? Next(RecurrenceRule rule, DateOnly from)
    {
        ArgumentNullException.ThrowIfNull(rule);

        // Count na regule bieżącego wystąpienia liczy je samo, więc jedynka znaczy
        // „to było ostatnie".
        if (rule.Count is <= 1)
        {
            return null;
        }

        var next = rule.Kind switch
        {
            RecurrenceKind.Daily => from.AddDays(1),
            RecurrenceKind.EveryNDays => from.AddDays(rule.Interval),
            RecurrenceKind.Weekly => NextWeekly(rule, from),
            RecurrenceKind.Monthly => NextByMonths(rule, from, rule.Interval),
            RecurrenceKind.Yearly => NextByMonths(rule, from, rule.Interval * 12),
            _ => from.AddDays(1),
        };

        return rule.Until is { } koniec && next > koniec ? null : next;
    }

    /// <summary>Wystąpienie razem z regułą, która ma pójść na nie dalej.</summary>
    public readonly record struct Occurrence(DateOnly Date, RecurrenceRule Rule);

    /// <summary>
    /// Pierwsze wystąpienie późniejsze od <paramref name="from"/> i **nie wcześniejsze**
    /// niż <paramref name="floor"/>. Wystąpienia przeskoczone po drodze zużywają licznik.
    /// </summary>
    /// <remarks>
    /// Potrzebne przy <see cref="OnMissed.Skip"/>: tydzień bez otwierania aplikacji ma
    /// dać jedno wystąpienie na dziś, a nie siedem utworzonych i wyrzuconych po kolei.
    /// Nagrobek każdego przeskoczonego dnia nie jest niczyją informacją, a rozjechałby
    /// się po wszystkich urządzeniach.
    /// </remarks>
    public static Occurrence? NextFrom(RecurrenceRule rule, DateOnly from, DateOnly floor)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var biezaca = rule;
        var baza = from;

        // Każdy rodzaj posuwa datę do przodu, więc pętla i tak się kończy. Ogranicznik
        // jest na wypadek reguły z nowszej wersji aplikacji, która tej własności nie ma.
        for (var i = 0; i < 10_000; i++)
        {
            if (Next(biezaca, baza) is not { } data)
            {
                return null;
            }

            biezaca = biezaca.Advance();

            if (data >= floor)
            {
                return new Occurrence(data, biezaca);
            }

            baza = data;
        }

        return null;
    }

    private static DateOnly NextWeekly(RecurrenceRule rule, DateOnly from)
    {
        // Pusty zbiór dni konstruktor odrzuca, ale reguła może przyjść z nowszej wersji
        // aplikacji przez synchronizację. Obrona: dzień tygodnia daty bazowej. Chodzi
        // o to, żeby zepsuta reguła dała zły rytm, a nie zapętliła aplikację.
        var dni = rule.DaysOfWeek == Weekdays.None ? from.DayOfWeek.ToFlag() : rule.DaysOfWeek;

        var tydzienBazowy = StartOfWeek(from);

        // Najdalej siedem dni przy odstępie tygodniowym, a przy większym — tyle tygodni,
        // ile wynosi odstęp, plus zapas na zejście do właściwego dnia.
        var limit = 7 * (rule.Interval + 1);

        for (var i = 1; i <= limit; i++)
        {
            var kandydat = from.AddDays(i);

            if (!dni.Includes(kandydat.DayOfWeek))
            {
                continue;
            }

            // Tygodnie liczone od tygodnia daty bazowej: „co drugi tydzień w poniedziałki
            // i czwartki" ma dać oba dni tego samego tygodnia, a potem przeskoczyć tydzień.
            var tygodni = (StartOfWeek(kandydat).DayNumber - tydzienBazowy.DayNumber) / 7;

            if (tygodni % rule.Interval == 0)
            {
                return kandydat;
            }
        }

        return from.AddDays(7 * rule.Interval);
    }

    private static DateOnly NextByMonths(RecurrenceRule rule, DateOnly from, int monthStep)
    {
        var dzien = rule.DayOfMonth ?? from.Day;

        // Najpierw ten sam miesiąc: reguła założona 3-go z dniem 15-go ma wypaść 15-go
        // tego samego miesiąca, a nie dopiero za miesiąc. W ustalonym rytmie data bazowa
        // jest już tym dniem, więc warunek nie zachodzi i krok idzie normalnie.
        var kandydat = InMonth(from.Year, from.Month, dzien);

        if (kandydat > from)
        {
            return kandydat;
        }

        var kolejny = new DateOnly(from.Year, from.Month, 1).AddMonths(monthStep);

        return InMonth(kolejny.Year, kolejny.Month, dzien);
    }

    private static DateOnly InMonth(int year, int month, int day)
    {
        // Przycięcie do długości miesiąca: 31 w kwietniu daje 30, w lutym 28 albo 29.
        // Dlatego „ostatniego każdego miesiąca" to po prostu dzień 31.
        var dlugosc = DateTime.DaysInMonth(year, month);
        return new DateOnly(year, month, Math.Min(day, dlugosc));
    }

    private static DateOnly StartOfWeek(DateOnly date) =>
        date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
}
