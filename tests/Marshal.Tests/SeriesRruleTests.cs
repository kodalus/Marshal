using FluentAssertions;
using Marshal.Application.Calendar;
using Marshal.Domain.Recurrence;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Rytm serii przełożony na regułę powtarzania iCal — tę, którą rozwija Google.
/// </summary>
/// <remarks>
/// Testy pilnują przede wszystkim tego, czego nie da się sprawdzić bez Google: że
/// przekład nie przesuwa dni. Reguła, która wygląda poprawnie, a wypada w inne dni niż
/// nasze okno, jest gorsza od braku odbicia — bo w cudzym kalendarzu stoi wtedy rozkład,
/// którego nikt nie obiecał, i nic tego nie pokazuje po naszej stronie.
/// </remarks>
public sealed class SeriesRruleTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    private static string Only(RecurrenceRule rule, DateOnly? last = null, TimeOnly? time = null) =>
        SeriesRrule.Lines(rule, last, time, Warsaw).Should().ContainSingle().Subject;

    [Fact]
    public void Codziennie_to_sama_czestotliwosc_bez_odstepu()
    {
        Only(new RecurrenceRule(RecurrenceKind.Daily)).Should().Be("RRULE:FREQ=DAILY");
    }

    [Fact]
    public void Co_ile_dni_to_czestotliwosc_dzienna_z_odstepem()
    {
        // Zaczepienie podane wprost, bo „co N dni" domyślnie liczy się **od wykonania**
        // (spec 5.7) — a takiego rytmu nie da się zapisać regułą i odbicia nie dostaje.
        // Reguła wychodzi więc tylko z tego, co ktoś świadomie zaczepił na kalendarzu.
        Only(new RecurrenceRule(
                RecurrenceKind.EveryNDays, interval: 3, anchor: RecurrenceAnchor.FromScheduled))
            .Should().Be("RRULE:FREQ=DAILY;INTERVAL=3");
    }

    [Fact]
    public void Dni_robocze_ida_skrotami_w_kolejnosci_tygodnia()
    {
        Only(new RecurrenceRule(RecurrenceKind.Weekly, daysOfWeek: Weekdays.Workdays))
            .Should().Be("RRULE:FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR;WKST=MO");
    }

    [Fact]
    public void Co_drugi_tydzien_liczy_tygodnie_od_poniedzialku()
    {
        // Granica tygodnia jest treścią, nie ozdobą: tam, gdzie tygodnie dzieli się od
        // niedzieli, wystąpienie z poniedziałku należy do poprzedniego tygodnia i cały
        // rytm „co drugi tydzień" wypada o tydzień obok naszego.
        Only(new RecurrenceRule(
                RecurrenceKind.Weekly, interval: 2, daysOfWeek: Weekdays.Monday | Weekdays.Thursday))
            .Should().Be("RRULE:FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,TH;WKST=MO");
    }

    [Fact]
    public void Trzydziesty_pierwszy_znaczy_ostatniego()
    {
        // U nas dzień miesiąca jest przycinany do jego długości, więc 31 znaczy
        // „ostatniego". W regule iCal to jest −1: samo 31 dałoby rytm pomijający luty,
        // kwiecień, czerwiec, wrzesień i listopad.
        Only(new RecurrenceRule(RecurrenceKind.Monthly, dayOfMonth: RecurrenceRule.LastDay))
            .Should().Be("RRULE:FREQ=MONTHLY;BYMONTHDAY=-1");
    }

    [Fact]
    public void Dzien_miesiaca_idzie_wprost()
    {
        Only(new RecurrenceRule(RecurrenceKind.Monthly, dayOfMonth: 10))
            .Should().Be("RRULE:FREQ=MONTHLY;BYMONTHDAY=10");
    }

    [Fact]
    public void Rok_to_czestotliwosc_roczna()
    {
        Only(new RecurrenceRule(RecurrenceKind.Yearly, interval: 2))
            .Should().Be("RRULE:FREQ=YEARLY;INTERVAL=2");
    }

    [Fact]
    public void Koniec_wydarzenia_calodniowego_to_sama_data()
    {
        Only(new RecurrenceRule(RecurrenceKind.Daily), last: new DateOnly(2026, 6, 30))
            .Should().Be("RRULE:FREQ=DAILY;UNTIL=20260630");
    }

    [Fact]
    public void Koniec_wydarzenia_z_godzina_to_chwila_w_czasie_uniwersalnym()
    {
        // Postać końca musi zgadzać się z postacią początku — sama data przy całodniowym,
        // chwila uniwersalna przy wydarzeniu z godziną — bo pomylenie tych dwóch Google
        // odrzuca. Trzydziestego czerwca Warszawa ma czas letni, czyli dwie godziny
        // przed uniwersalnym: ósma rano to szósta uniwersalna.
        Only(
            new RecurrenceRule(RecurrenceKind.Daily),
            last: new DateOnly(2026, 6, 30),
            time: new TimeOnly(8, 0))
            .Should().Be("RRULE:FREQ=DAILY;UNTIL=20260630T060000Z");
    }

    [Fact]
    public void Rytm_od_wykonania_nie_ma_czego_wyslac()
    {
        // „Co 3 dni od wykonania" nie ma dat, dopóki poprzednie wystąpienie nie zostanie
        // odhaczone. Wysłana reguła byłaby rozkładem, którego nikt nie obiecał.
        SeriesRrule.Lines(
            new RecurrenceRule(
                RecurrenceKind.EveryNDays, interval: 3, anchor: RecurrenceAnchor.FromCompletion),
            null,
            null,
            Warsaw)
            .Should().BeNull();
    }
}
