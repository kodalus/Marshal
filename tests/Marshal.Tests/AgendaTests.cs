using FluentAssertions;
using Marshal.Application.Calendar;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Układanie siatki godzinowej. Nakładanie się wydarzeń i przejścia przez północ to
/// dwie rzeczy, które w kalendarzach wychodzą źle i widać to dopiero okiem — więc
/// muszą dać się sprawdzić bez rysowania.
/// </summary>
public sealed class AgendaTests
{
    private static readonly TimeSpan NewZone = TimeSpan.FromHours(2);
    private static readonly DateOnly Today = new(2026, 9, 16);

    private static DateTimeOffset O(string day, int hour, int minuta = 0) =>
        new(DateOnly.Parse(day).ToDateTime(new TimeOnly(hour, minuta)), NewZone);

    private static AgendaEntry NewEvent(
        string title, string day, int od, int toHour, int odMin = 0, int doMin = 0) =>
        new(title, O(day, od, odMin), O(day, toHour, doMin), false, AgendaKind.Event, null, null);

    [Fact]
    public void Pusty_zakres_daje_dni_bez_zawartosci()
    {
        var days = Agenda.Build([], Today, 3);

        days.Should().HaveCount(3);
        days.Should().AllSatisfy(d => d.Timed.Should().BeEmpty());
        days[2].Date.Should().Be(Today.AddDays(2));
    }

    [Fact]
    public void Wydarzenie_trafia_na_swoj_dzien()
    {
        var days = Agenda.Build([NewEvent("spotkanie", "2026-09-17", 10, 11)], Today, 3);

        days[0].Timed.Should().BeEmpty();
        days[1].Timed.Should().ContainSingle();
        days[1].Timed[0].Entry.Title.Should().Be("spotkanie");
    }

    [Fact]
    public void Wydarzenia_rozlaczne_dziela_jedna_kolumne()
    {
        // Dwa krótkie spotkania jedno po drugim nie kolidują, więc nie mają powodu
        // zwężać się do połowy szerokości.
        var days = Agenda.Build(
            [NewEvent("pierwsze", "2026-09-16", 9, 10), NewEvent("drugie", "2026-09-16", 10, 11)],
            Today, 1);

        days[0].Timed.Should().AllSatisfy(s => s.Columns.Should().Be(1));
        days[0].Timed.Should().AllSatisfy(s => s.Column.Should().Be(0));
    }

    [Fact]
    public void Wydarzenia_nakladajace_sie_dostaja_osobne_kolumny()
    {
        var days = Agenda.Build(
            [NewEvent("pierwsze", "2026-09-16", 9, 11), NewEvent("drugie", "2026-09-16", 10, 12)],
            Today, 1);

        days[0].Timed.Should().HaveCount(2);
        days[0].Timed.Select(s => s.Column).Should().Equal(0, 1);
        days[0].Timed.Should().AllSatisfy(s => s.Columns.Should().Be(2));
    }

    [Fact]
    public void Trzy_nakladajace_sie_dostaja_trzy_kolumny()
    {
        var days = Agenda.Build(
            [
                NewEvent("a", "2026-09-16", 9, 12),
                NewEvent("b", "2026-09-16", 10, 12),
                NewEvent("c", "2026-09-16", 11, 12),
            ],
            Today, 1);

        days[0].Timed.Should().AllSatisfy(s => s.Columns.Should().Be(3));
        days[0].Timed.Select(s => s.Column).Should().Equal(0, 1, 2);
    }

    [Fact]
    public void Kolumny_licza_sie_w_gronie_a_nie_na_caly_dzien()
    {
        // Poranne spotkanie nie ma powodu zwężać się do jednej trzeciej dlatego,
        // że wieczorem coś się na siebie nakłada.
        var days = Agenda.Build(
            [
                NewEvent("rano", "2026-09-16", 8, 9),
                NewEvent("wieczór a", "2026-09-16", 18, 20),
                NewEvent("wieczór b", "2026-09-16", 19, 21),
            ],
            Today, 1);

        var rano = days[0].Timed.Single(s => s.Entry.Title == "rano");
        rano.Columns.Should().Be(1);
        days[0].Timed.Where(s => s.Entry.Title.StartsWith("wieczór", StringComparison.Ordinal))
            .Should().AllSatisfy(s => s.Columns.Should().Be(2));
    }

    [Fact]
    public void Zwolniona_kolumna_jest_uzywana_ponownie()
    {
        // a 9–10, b 9–12, c 10–11. Trzy wydarzenia, ale w danej chwili najwyżej dwa
        // naraz — więc dwie kolumny, a c wchodzi do tej, którą zwolniło a.
        // Numer kolumny jest szczegółem układu; istotne jest, że c ją po a dziedziczy,
        // a nie zakłada trzeciej.
        var days = Agenda.Build(
            [
                NewEvent("a", "2026-09-16", 9, 10),
                NewEvent("b", "2026-09-16", 9, 12),
                NewEvent("c", "2026-09-16", 10, 11),
            ],
            Today, 1);

        var a = days[0].Timed.Single(s => s.Entry.Title == "a");
        var c = days[0].Timed.Single(s => s.Entry.Title == "c");

        c.Column.Should().Be(a.Column);
        days[0].Timed.Should().AllSatisfy(s => s.Columns.Should().Be(2));
    }

    [Fact]
    public void Dwa_rozlaczne_po_wspolnym_nie_mnoza_kolumn()
    {
        // Długie wydarzenie i pod nim dwa krótkie jedno po drugim: dwie kolumny,
        // nie trzy. Bez ponownego użycia kolumny dzień z wieloma krótkimi punktami
        // zwęziłby się do nitek.
        var days = Agenda.Build(
            [
                NewEvent("długie", "2026-09-16", 9, 15),
                NewEvent("krótkie a", "2026-09-16", 9, 10),
                NewEvent("krótkie b", "2026-09-16", 11, 12),
                NewEvent("krótkie c", "2026-09-16", 13, 14),
            ],
            Today, 1);

        days[0].Timed.Should().AllSatisfy(s => s.Columns.Should().Be(2));
    }

    [Fact]
    public void Wydarzenie_przez_polnoc_pojawia_sie_na_obu_dniach_przyciete()
    {
        // Inaczej na siatce drugiego dnia zaczynałoby się „minus godzinę temu".
        var przezPolnoc = new AgendaEntry(
            "nocne", O("2026-09-16", 23), O("2026-09-17", 1), false, AgendaKind.Event, null, null);

        var days = Agenda.Build([przezPolnoc], Today, 2);

        days[0].Timed.Should().ContainSingle();
        days[0].Timed[0].Entry.StartHour.Should().Be(23);
        days[0].Timed[0].Entry.Hours.Should().Be(1);

        days[1].Timed.Should().ContainSingle();
        days[1].Timed[0].Entry.StartHour.Should().Be(0);
        days[1].Timed[0].Entry.Hours.Should().Be(1);
    }

    [Fact]
    public void Koniec_o_polnocy_nie_wchodzi_na_nastepny_dzien()
    {
        // Spotkanie do 24:00 kończy się dziś, a nie zaczyna jutro.
        var doPolnocy = new AgendaEntry(
            "wieczorne", O("2026-09-16", 22), O("2026-09-17", 0), false, AgendaKind.Event, null, null);

        var days = Agenda.Build([doPolnocy], Today, 2);

        days[0].Timed.Should().ContainSingle();
        days[1].Timed.Should().BeEmpty();
    }

    [Fact]
    public void Calodniowe_ida_na_pasek_a_nie_na_siatke()
    {
        var allDay = new AgendaEntry(
            "urlop", O("2026-09-16", 0), O("2026-09-18", 0), true, AgendaKind.Event, null, null);

        var days = Agenda.Build([allDay], Today, 3);

        days[0].AllDay.Should().ContainSingle();
        days[1].AllDay.Should().ContainSingle();
        days[2].AllDay.Should().BeEmpty();
        days.Should().AllSatisfy(d => d.Timed.Should().BeEmpty());
    }

    [Fact]
    public void Bardzo_krotkie_wydarzenie_ma_wysokosc_minimalna()
    {
        // Pięciominutowe spotkanie narysowane co do proporcji byłoby kreską,
        // w którą nie da się trafić palcem.
        var days = Agenda.Build([NewEvent("szybkie", "2026-09-16", 9, 9, 0, 5)], Today, 1);

        days[0].Timed[0].Entry.Hours.Should().Be(0.25);
    }

    [Fact]
    public void Zadania_i_wydarzenia_leza_na_tej_samej_siatce()
    {
        var task = new AgendaEntry(
            "zadzwonić", O("2026-09-16", 10), O("2026-09-16", 10, 30),
            false, AgendaKind.Task, null, Guid.CreateVersion7());

        var days = Agenda.Build([NewEvent("spotkanie", "2026-09-16", 10, 11), task], Today, 1);

        days[0].Timed.Should().HaveCount(2);
        days[0].Timed.Should().AllSatisfy(s => s.Columns.Should().Be(2));
        days[0].Timed.Select(s => s.Entry.Kind).Should().Contain(AgendaKind.Task);
    }
}
