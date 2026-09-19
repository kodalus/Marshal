using FluentAssertions;
using Marshal.Domain.Recurrence;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Przypadki brzegowe wymienione w spec 8.4, plus rytmy podstawowe.
/// </summary>
public sealed class RecurrenceScheduleTests
{
    private static DateOnly D(string iso) => DateOnly.Parse(iso);

    [Fact]
    public void Codziennie_daje_nastepny_dzien()
    {
        RecurrenceSchedule.Next(new RecurrenceRule(RecurrenceKind.Daily), D("2026-09-16"))
            .Should().Be(D("2026-09-17"));
    }

    [Fact]
    public void Co_n_dni_dodaje_odstep()
    {
        RecurrenceSchedule.Next(
                new RecurrenceRule(RecurrenceKind.EveryNDays, interval: 3), D("2026-09-16"))
            .Should().Be(D("2026-09-19"));
    }

    [Fact]
    public void Tygodniowo_idzie_do_najblizszego_wskazanego_dnia()
    {
        // Środa 16.09.2026 → czwartek.
        var rule = new RecurrenceRule(
            RecurrenceKind.Weekly, daysOfWeek: Weekdays.Monday | Weekdays.Thursday);

        RecurrenceSchedule.Next(rule, D("2026-09-16")).Should().Be(D("2026-09-17"));
    }

    [Fact]
    public void Tygodniowo_przechodzi_przez_koniec_tygodnia()
    {
        var rule = new RecurrenceRule(RecurrenceKind.Weekly, daysOfWeek: Weekdays.Monday);

        RecurrenceSchedule.Next(rule, D("2026-09-14")).Should().Be(D("2026-09-21"));
    }

    [Fact]
    public void Co_drugi_tydzien_bierze_oba_dni_tego_samego_tygodnia_a_potem_przeskakuje()
    {
        // „Co dwa tygodnie w poniedziałki i czwartki" znaczy oba dni w tym samym
        // tygodniu, nie co dziesięć dni na przemian.
        var rule = new RecurrenceRule(
            RecurrenceKind.Weekly, interval: 2, daysOfWeek: Weekdays.Monday | Weekdays.Thursday);

        var czwartek = RecurrenceSchedule.Next(rule, D("2026-09-14"));
        czwartek.Should().Be(D("2026-09-17"));

        RecurrenceSchedule.Next(rule, czwartek!.Value).Should().Be(D("2026-09-28"));
    }

    [Fact]
    public void Tygodniowo_z_pustym_zbiorem_dni_jest_odrzucane_przy_zapisie()
    {
        var utworz = () => new RecurrenceRule(RecurrenceKind.Weekly);

        utworz.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Miesiecznie_trzyma_dzien_miesiaca()
    {
        var rule = new RecurrenceRule(RecurrenceKind.Monthly, dayOfMonth: 15);

        RecurrenceSchedule.Next(rule, D("2026-09-15")).Should().Be(D("2026-10-15"));
    }

    [Fact]
    public void Miesiecznie_wypada_jeszcze_w_tym_miesiacu_gdy_dzien_dopiero_nadchodzi()
    {
        // Reguła założona 3-go z dniem 15-go ma wypaść 15-go tego samego miesiąca.
        var rule = new RecurrenceRule(RecurrenceKind.Monthly, dayOfMonth: 15);

        RecurrenceSchedule.Next(rule, D("2026-09-03")).Should().Be(D("2026-09-15"));
    }

    [Theory]
    [InlineData("2026-01-31", "2026-02-28")]
    [InlineData("2026-03-31", "2026-04-30")]
    [InlineData("2028-01-31", "2028-02-29")]
    public void Trzydziesty_pierwszy_przycina_sie_do_dlugosci_miesiaca(string baza, string oczekiwana)
    {
        // Dlatego „ostatniego każdego miesiąca" nie potrzebuje osobnego pola.
        var rule = new RecurrenceRule(RecurrenceKind.Monthly, dayOfMonth: RecurrenceRule.LastDay);

        RecurrenceSchedule.Next(rule, D(baza)).Should().Be(D(oczekiwana));
    }

    [Fact]
    public void Przyciecie_nie_zjada_dnia_na_stale()
    {
        // Po lutym rytm musi wrócić do 31-go, a nie zostać na 28-ym do końca świata.
        var rule = new RecurrenceRule(RecurrenceKind.Monthly, dayOfMonth: RecurrenceRule.LastDay);

        RecurrenceSchedule.Next(rule, D("2026-02-28")).Should().Be(D("2026-03-31"));
    }

    [Fact]
    public void Dwudziesty_dziewiaty_lutego_w_roku_zwyklym_schodzi_na_dwudziesty_osmy()
    {
        var rule = new RecurrenceRule(RecurrenceKind.Yearly);

        RecurrenceSchedule.Next(rule, D("2028-02-29")).Should().Be(D("2029-02-28"));
    }

    [Fact]
    public void Rocznie_trzyma_miesiac_daty_bazowej()
    {
        RecurrenceSchedule.Next(new RecurrenceRule(RecurrenceKind.Yearly), D("2026-07-03"))
            .Should().Be(D("2027-07-03"));
    }

    [Fact]
    public void Data_konca_ucina_serie()
    {
        var rule = new RecurrenceRule(RecurrenceKind.Daily, until: D("2026-09-17"));

        RecurrenceSchedule.Next(rule, D("2026-09-16")).Should().Be(D("2026-09-17"));
        RecurrenceSchedule.Next(rule, D("2026-09-17")).Should().BeNull();
    }

    [Fact]
    public void Ostatnie_wystapienie_nie_ma_nastepnika()
    {
        var rule = new RecurrenceRule(RecurrenceKind.Daily, count: 1);

        RecurrenceSchedule.Next(rule, D("2026-09-16")).Should().BeNull();
    }

    [Fact]
    public void Licznik_wystapien_maleje_o_jeden_na_wystapienie()
    {
        var rule = new RecurrenceRule(RecurrenceKind.Daily, count: 3);

        rule.Advance().Count.Should().Be(2);
        rule.Advance().Advance().Count.Should().Be(1);
        RecurrenceSchedule.Next(rule.Advance().Advance(), D("2026-09-16")).Should().BeNull();
    }

    [Fact]
    public void Seria_bez_konca_zostaje_bez_konca()
    {
        var rule = new RecurrenceRule(RecurrenceKind.Daily);

        rule.Advance().Count.Should().BeNull();
    }

    [Theory]
    [InlineData(RecurrenceKind.Daily, RecurrenceAnchor.FromCompletion)]
    [InlineData(RecurrenceKind.EveryNDays, RecurrenceAnchor.FromCompletion)]
    [InlineData(RecurrenceKind.Weekly, RecurrenceAnchor.FromScheduled)]
    [InlineData(RecurrenceKind.Monthly, RecurrenceAnchor.FromScheduled)]
    [InlineData(RecurrenceKind.Yearly, RecurrenceAnchor.FromScheduled)]
    public void Domyslne_zaczepienie_wynika_z_rodzaju(RecurrenceKind kind, RecurrenceAnchor waiting)
    {
        RecurrenceRule.DefaultAnchorFor(kind).Should().Be(waiting);
    }

    [Fact]
    public void Zmiana_czasu_nie_przesuwa_dnia()
    {
        // Ostatnia niedziela października — koniec czasu letniego w Polsce.
        // Rytm liczony na chwilach potrafiłby tu wypaść dzień wcześniej o 23:00;
        // na DateOnly ten przypadek brzegowy nie istnieje.
        var rule = new RecurrenceRule(RecurrenceKind.Weekly, daysOfWeek: Weekdays.Monday);

        RecurrenceSchedule.Next(rule, D("2026-10-19")).Should().Be(D("2026-10-26"));
    }

    [Fact]
    public void Odstep_niedodatni_jest_odrzucany()
    {
        var utworz = () => new RecurrenceRule(RecurrenceKind.EveryNDays, interval: 0);

        utworz.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Regula_przechodzi_przez_zapis_i_odczyt_bez_zmian()
    {
        var rule = new RecurrenceRule(
            RecurrenceKind.Weekly,
            interval: 2,
            daysOfWeek: Weekdays.Monday | Weekdays.Friday,
            anchor: RecurrenceAnchor.FromCompletion,
            onMissed: OnMissed.Skip,
            until: D("2027-01-01"),
            count: 10);

        RecurrenceRule.FromJson(rule.ToJson()).Should().Be(rule);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("to nie jest JSON")]
    [InlineData("{\"Kind\":\"Weekly\",\"DaysOfWeek\":\"None\"}")]
    [InlineData("{\"Kind\":\"Daily\",\"Interval\":0}")]
    public void Zepsuta_regula_daje_pustke_zamiast_wyjatku(string? json)
    {
        // Wpis z nowszej wersji aplikacji nie może wywrócić scalania (spec 9.4).
        RecurrenceRule.FromJson(json).Should().BeNull();
    }
}
