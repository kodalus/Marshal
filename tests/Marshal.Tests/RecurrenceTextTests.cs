using FluentAssertions;
using Marshal.Application.UseCases;
using Marshal.Domain.Primitives;
using Marshal.Domain.Recurrence;
using Marshal.Domain.Tasks;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Zdanie opisujące rytm. Pomyłka w odmianie liczebnika nie daje żadnego objawu
/// poza tym, że zdanie brzmi źle — a właśnie to zdanie jest jedynym sposobem
/// sprawdzenia wpisanego rytmu bez czekania dwóch tygodni na wynik.
/// </summary>
public sealed class RecurrenceTextTests
{
    private static DateOnly D(string iso) => DateOnly.Parse(iso);

    [Fact]
    public void Brak_reguly_jest_nazwany_wprost()
    {
        RecurrenceText.Describe(null).Should().Be("nie powtarza się");
    }

    [Fact]
    public void Codziennie()
    {
        RecurrenceText.Describe(new RecurrenceRule(RecurrenceKind.Daily))
            .Should().Be("codziennie");
    }

    [Fact]
    public void Co_kilka_dni()
    {
        RecurrenceText.Describe(new RecurrenceRule(RecurrenceKind.EveryNDays, interval: 3))
            .Should().Be("co 3 dni");
    }

    [Fact]
    public void Co_jeden_dzien_to_po_prostu_codziennie()
    {
        // „co 1 dni" jest kalectwem, a wyjdzie z tej samej reguły co reszta.
        RecurrenceText.Describe(new RecurrenceRule(RecurrenceKind.EveryNDays, interval: 1))
            .Should().Be("codziennie");
    }

    [Fact]
    public void Tygodniowo_z_jednym_dniem()
    {
        RecurrenceText.Describe(
                new RecurrenceRule(RecurrenceKind.Weekly, daysOfWeek: Weekdays.Monday))
            .Should().Be("co tydzień, w poniedziałki");
    }

    [Fact]
    public void Tygodniowo_z_dwoma_dniami_laczy_je_spojnikiem()
    {
        RecurrenceText.Describe(
                new RecurrenceRule(
                    RecurrenceKind.Weekly, daysOfWeek: Weekdays.Monday | Weekdays.Thursday))
            .Should().Be("co tydzień, w poniedziałki i czwartki");
    }

    [Fact]
    public void Tygodniowo_z_wieloma_dniami_wylicza_przecinkami()
    {
        RecurrenceText.Describe(new RecurrenceRule(RecurrenceKind.Weekly, daysOfWeek: Weekdays.Workdays))
            .Should().Be("co tydzień, w poniedziałki, wtorki, środy, czwartki i piątki");
    }

    [Theory]
    [InlineData(2, "co 2 tygodnie")]
    [InlineData(3, "co 3 tygodnie")]
    [InlineData(4, "co 4 tygodnie")]
    [InlineData(5, "co 5 tygodni")]
    [InlineData(12, "co 12 tygodni")]
    [InlineData(13, "co 13 tygodni")]
    [InlineData(22, "co 22 tygodnie")]
    public void Odmiana_tygodni_idzie_za_regula_z_wyjatkiem_nastek(int ile, string oczekiwany)
    {
        // Dwa do czterech mają swoją formę, reszta inną — ale dwanaście, trzynaście
        // i czternaście idą z resztą, mimo że kończą się na dwa, trzy i cztery.
        RecurrenceText.Describe(
                new RecurrenceRule(RecurrenceKind.Weekly, interval: ile, daysOfWeek: Weekdays.Monday))
            .Should().StartWith(oczekiwany);
    }

    [Theory]
    [InlineData(2, "co 2 miesiące")]
    [InlineData(5, "co 5 miesięcy")]
    [InlineData(13, "co 13 miesięcy")]
    public void Odmiana_miesiecy(int ile, string oczekiwany)
    {
        RecurrenceText.Describe(new RecurrenceRule(RecurrenceKind.Monthly, interval: ile))
            .Should().StartWith(oczekiwany);
    }

    [Theory]
    [InlineData(2, "co 2 lata")]
    [InlineData(5, "co 5 lat")]
    [InlineData(14, "co 14 lat")]
    public void Odmiana_lat(int ile, string oczekiwany)
    {
        RecurrenceText.Describe(new RecurrenceRule(RecurrenceKind.Yearly, interval: ile))
            .Should().Be(oczekiwany);
    }

    [Fact]
    public void Miesiecznie_z_dniem()
    {
        RecurrenceText.Describe(new RecurrenceRule(RecurrenceKind.Monthly, dayOfMonth: 15))
            .Should().Be("co miesiąc, 15. dnia");
    }

    [Fact]
    public void Trzydziesty_pierwszy_nazywa_sie_ostatnim()
    {
        // Bo tym jest: dzień miesiąca przycina się do jego długości.
        RecurrenceText.Describe(
                new RecurrenceRule(RecurrenceKind.Monthly, dayOfMonth: RecurrenceRule.LastDay))
            .Should().Be("co miesiąc, ostatniego dnia");
    }

    [Fact]
    public void Zaczepienie_dopisywane_tylko_gdy_odbiega_od_domyslnego()
    {
        // Powtarzanie oczywistości w każdym zdaniu zamienia je w szum.
        RecurrenceText.Describe(new RecurrenceRule(RecurrenceKind.Daily))
            .Should().NotContain("licząc");

        RecurrenceText.Describe(
                new RecurrenceRule(RecurrenceKind.Weekly, daysOfWeek: Weekdays.Monday))
            .Should().NotContain("licząc");
    }

    [Fact]
    public void Zaczepienie_od_wykonania_przy_rytmie_kalendarzowym_jest_nazwane()
    {
        RecurrenceText.Describe(new RecurrenceRule(
                RecurrenceKind.Weekly,
                daysOfWeek: Weekdays.Monday,
                anchor: RecurrenceAnchor.FromCompletion))
            .Should().Be("co tydzień, w poniedziałki, licząc od wykonania");
    }

    [Fact]
    public void Zaczepienie_na_planie_przy_rytmie_bez_kalendarza_jest_nazwane()
    {
        RecurrenceText.Describe(new RecurrenceRule(
                RecurrenceKind.EveryNDays,
                interval: 3,
                anchor: RecurrenceAnchor.FromScheduled))
            .Should().Be("co 3 dni, licząc od planu");
    }

    [Fact]
    public void Data_konca_jest_w_zdaniu()
    {
        RecurrenceText.Describe(new RecurrenceRule(RecurrenceKind.Daily, until: D("2027-01-01")))
            .Should().Be("codziennie, do 2027-01-01");
    }

    [Theory]
    [InlineData(1, "codziennie, jeszcze 1 raz")]
    [InlineData(3, "codziennie, jeszcze 3 razy")]
    [InlineData(7, "codziennie, jeszcze 7 razy")]
    public void Liczba_pozostalych_wystapien_jest_w_zdaniu(int ile, string oczekiwane)
    {
        RecurrenceText.Describe(new RecurrenceRule(RecurrenceKind.Daily, count: ile))
            .Should().Be(oczekiwane);
    }

    // --- etykiety stanu ------------------------------------------------------

    private TaskItem Zadanie()
    {
        var hlc = Hlc.Zero("test");
        var zadanie = TaskItem.Capture("Wynieść śmieci", DateTimeOffset.UnixEpoch, hlc);
        zadanie.MakeNext(Guid.CreateVersion7(), Hlc.Next(hlc, 1));
        return zadanie;
    }

    [Fact]
    public void Zadanie_bez_niczego_nie_ma_etykiet()
    {
        RecurrenceText.Badges(Zadanie(), D("2026-09-16")).Should().BeEmpty();
    }

    [Fact]
    public void Termin_w_przyszlosci_i_w_przeszlosci_brzmia_inaczej()
    {
        var zadanie = Zadanie();
        zadanie.SetDeadline(D("2026-09-20"), Hlc.Next(Hlc.Zero("test"), 99));

        RecurrenceText.Badges(zadanie, D("2026-09-16")).Should().Contain("termin 2026-09-20");
        RecurrenceText.Badges(zadanie, D("2026-09-25")).Should().Contain("po terminie (2026-09-20)");
    }

    [Fact]
    public void Pojedyncze_przesuniecie_nie_jest_pokazywane()
    {
        // Pierwsze przesunięcie zdarza się każdemu i nie niesie informacji. Pokazane
        // byłoby wyrzutem bez treści.
        var hlc = Hlc.Zero("test");
        var zadanie = TaskItem.Capture("Zadzwonić", DateTimeOffset.UnixEpoch, hlc);
        zadanie.Schedule(Guid.CreateVersion7(), D("2026-09-10"), Hlc.Next(hlc, 1));
        zadanie.RollTo(D("2026-09-16"), Hlc.Next(hlc, 2));

        RecurrenceText.Badges(zadanie, D("2026-09-16"))
            .Should().NotContain(e => e.StartsWith("przesunięte", StringComparison.Ordinal));
    }

    [Fact]
    public void Wielokrotne_przesuniecie_jest_liczone_bez_oceny()
    {
        var hlc = Hlc.Zero("test");
        var zadanie = TaskItem.Capture("Zadzwonić", DateTimeOffset.UnixEpoch, hlc);
        zadanie.Schedule(Guid.CreateVersion7(), D("2026-09-10"), Hlc.Next(hlc, 1));

        for (var i = 2; i <= 5; i++)
        {
            zadanie.RollTo(D("2026-09-16"), Hlc.Next(hlc, i));
        }

        RecurrenceText.Badges(zadanie, D("2026-09-16")).Should().Contain("przesunięte 4 razy");
    }

    [Fact]
    public void Zaleglosc_nazywa_pierwszy_przegapiony_dzien()
    {
        var hlc = Hlc.Zero("test");
        var zadanie = TaskItem.Capture("Zapłacić", DateTimeOffset.UnixEpoch, hlc);
        zadanie.Schedule(Guid.CreateVersion7(), D("2026-09-09"), Hlc.Next(hlc, 1));
        zadanie.CarryTo(D("2026-09-16"), Hlc.Next(hlc, 2));

        RecurrenceText.Badges(zadanie, D("2026-09-16")).Should().Contain("zaległe od 2026-09-09");
    }
}
