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
    private static readonly TimeSpan Strefa = TimeSpan.FromHours(2);
    private static readonly DateOnly Dzis = new(2026, 9, 16);

    private static DateTimeOffset O(string dzien, int godzina, int minuta = 0) =>
        new(DateOnly.Parse(dzien).ToDateTime(new TimeOnly(godzina, minuta)), Strefa);

    private static AgendaEntry Wydarzenie(
        string tytul, string dzien, int od, int doGodz, int odMin = 0, int doMin = 0) =>
        new(tytul, O(dzien, od, odMin), O(dzien, doGodz, doMin), false, AgendaKind.Event, null, null);

    [Fact]
    public void Pusty_zakres_daje_dni_bez_zawartosci()
    {
        var dni = Agenda.Build([], Dzis, 3);

        dni.Should().HaveCount(3);
        dni.Should().AllSatisfy(d => d.Timed.Should().BeEmpty());
        dni[2].Date.Should().Be(Dzis.AddDays(2));
    }

    [Fact]
    public void Wydarzenie_trafia_na_swoj_dzien()
    {
        var dni = Agenda.Build([Wydarzenie("spotkanie", "2026-09-17", 10, 11)], Dzis, 3);

        dni[0].Timed.Should().BeEmpty();
        dni[1].Timed.Should().ContainSingle();
        dni[1].Timed[0].Entry.Title.Should().Be("spotkanie");
    }

    [Fact]
    public void Wydarzenia_rozlaczne_dziela_jedna_kolumne()
    {
        // Dwa krótkie spotkania jedno po drugim nie kolidują, więc nie mają powodu
        // zwężać się do połowy szerokości.
        var dni = Agenda.Build(
            [Wydarzenie("pierwsze", "2026-09-16", 9, 10), Wydarzenie("drugie", "2026-09-16", 10, 11)],
            Dzis, 1);

        dni[0].Timed.Should().AllSatisfy(s => s.Columns.Should().Be(1));
        dni[0].Timed.Should().AllSatisfy(s => s.Column.Should().Be(0));
    }

    [Fact]
    public void Wydarzenia_nakladajace_sie_dostaja_osobne_kolumny()
    {
        var dni = Agenda.Build(
            [Wydarzenie("pierwsze", "2026-09-16", 9, 11), Wydarzenie("drugie", "2026-09-16", 10, 12)],
            Dzis, 1);

        dni[0].Timed.Should().HaveCount(2);
        dni[0].Timed.Select(s => s.Column).Should().Equal(0, 1);
        dni[0].Timed.Should().AllSatisfy(s => s.Columns.Should().Be(2));
    }

    [Fact]
    public void Trzy_nakladajace_sie_dostaja_trzy_kolumny()
    {
        var dni = Agenda.Build(
            [
                Wydarzenie("a", "2026-09-16", 9, 12),
                Wydarzenie("b", "2026-09-16", 10, 12),
                Wydarzenie("c", "2026-09-16", 11, 12),
            ],
            Dzis, 1);

        dni[0].Timed.Should().AllSatisfy(s => s.Columns.Should().Be(3));
        dni[0].Timed.Select(s => s.Column).Should().Equal(0, 1, 2);
    }

    [Fact]
    public void Kolumny_licza_sie_w_gronie_a_nie_na_caly_dzien()
    {
        // Poranne spotkanie nie ma powodu zwężać się do jednej trzeciej dlatego,
        // że wieczorem coś się na siebie nakłada.
        var dni = Agenda.Build(
            [
                Wydarzenie("rano", "2026-09-16", 8, 9),
                Wydarzenie("wieczór a", "2026-09-16", 18, 20),
                Wydarzenie("wieczór b", "2026-09-16", 19, 21),
            ],
            Dzis, 1);

        var rano = dni[0].Timed.Single(s => s.Entry.Title == "rano");
        rano.Columns.Should().Be(1);
        dni[0].Timed.Where(s => s.Entry.Title.StartsWith("wieczór", StringComparison.Ordinal))
            .Should().AllSatisfy(s => s.Columns.Should().Be(2));
    }

    [Fact]
    public void Zwolniona_kolumna_jest_uzywana_ponownie()
    {
        // a 9–10, b 9–12, c 10–11: c wchodzi po a, do tej samej kolumny.
        var dni = Agenda.Build(
            [
                Wydarzenie("a", "2026-09-16", 9, 10),
                Wydarzenie("b", "2026-09-16", 9, 12),
                Wydarzenie("c", "2026-09-16", 10, 11),
            ],
            Dzis, 1);

        var c = dni[0].Timed.Single(s => s.Entry.Title == "c");
        c.Column.Should().Be(0);
        dni[0].Timed.Should().AllSatisfy(s => s.Columns.Should().Be(2));
    }

    [Fact]
    public void Wydarzenie_przez_polnoc_pojawia_sie_na_obu_dniach_przyciete()
    {
        // Inaczej na siatce drugiego dnia zaczynałoby się „minus godzinę temu".
        var przezPolnoc = new AgendaEntry(
            "nocne", O("2026-09-16", 23), O("2026-09-17", 1), false, AgendaKind.Event, null, null);

        var dni = Agenda.Build([przezPolnoc], Dzis, 2);

        dni[0].Timed.Should().ContainSingle();
        dni[0].Timed[0].Entry.StartHour.Should().Be(23);
        dni[0].Timed[0].Entry.Hours.Should().Be(1);

        dni[1].Timed.Should().ContainSingle();
        dni[1].Timed[0].Entry.StartHour.Should().Be(0);
        dni[1].Timed[0].Entry.Hours.Should().Be(1);
    }

    [Fact]
    public void Koniec_o_polnocy_nie_wchodzi_na_nastepny_dzien()
    {
        // Spotkanie do 24:00 kończy się dziś, a nie zaczyna jutro.
        var doPolnocy = new AgendaEntry(
            "wieczorne", O("2026-09-16", 22), O("2026-09-17", 0), false, AgendaKind.Event, null, null);

        var dni = Agenda.Build([doPolnocy], Dzis, 2);

        dni[0].Timed.Should().ContainSingle();
        dni[1].Timed.Should().BeEmpty();
    }

    [Fact]
    public void Calodniowe_ida_na_pasek_a_nie_na_siatke()
    {
        var calodniowe = new AgendaEntry(
            "urlop", O("2026-09-16", 0), O("2026-09-18", 0), true, AgendaKind.Event, null, null);

        var dni = Agenda.Build([calodniowe], Dzis, 3);

        dni[0].AllDay.Should().ContainSingle();
        dni[1].AllDay.Should().ContainSingle();
        dni[2].AllDay.Should().BeEmpty();
        dni.Should().AllSatisfy(d => d.Timed.Should().BeEmpty());
    }

    [Fact]
    public void Bardzo_krotkie_wydarzenie_ma_wysokosc_minimalna()
    {
        // Pięciominutowe spotkanie narysowane co do proporcji byłoby kreską,
        // w którą nie da się trafić palcem.
        var dni = Agenda.Build([Wydarzenie("szybkie", "2026-09-16", 9, 9, 0, 5)], Dzis, 1);

        dni[0].Timed[0].Entry.Hours.Should().Be(0.25);
    }

    [Fact]
    public void Zadania_i_wydarzenia_leza_na_tej_samej_siatce()
    {
        var zadanie = new AgendaEntry(
            "zadzwonić", O("2026-09-16", 10), O("2026-09-16", 10, 30),
            false, AgendaKind.Task, null, Guid.CreateVersion7());

        var dni = Agenda.Build([Wydarzenie("spotkanie", "2026-09-16", 10, 11), zadanie], Dzis, 1);

        dni[0].Timed.Should().HaveCount(2);
        dni[0].Timed.Should().AllSatisfy(s => s.Columns.Should().Be(2));
        dni[0].Timed.Select(s => s.Entry.Kind).Should().Contain(AgendaKind.Task);
    }
}
