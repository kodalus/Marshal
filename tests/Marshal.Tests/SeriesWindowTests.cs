using FluentAssertions;
using Marshal.Domain.Primitives;
using Marshal.Domain.Recurrence;
using Marshal.Domain.Series;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Okno serii: które wystąpienia mają istnieć i czy dwa urządzenia dojdą do tych samych.
/// </summary>
/// <remarks>
/// Sedno całej przebudowy siedzi w jednym zdaniu: okno jest funkcją serii i dzisiejszej
/// daty. Poprzedni model liczył tożsamość następnika z poprzednika i z dnia — funkcja
/// deterministyczna, której wejścia nie były wspólne, bo dzień zależał od tego, kiedy
/// które urządzenie zostało otwarte. Testy pilnują przede wszystkim tego, że tutaj
/// wejściem jest wyłącznie seria i data.
/// </remarks>
public sealed class SeriesWindowTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 5, 9, 0, 0, TimeSpan.FromHours(2));

    private static readonly DateOnly Today = new(2026, 10, 5);

    private static Hlc Stamp() => new(Now.ToUnixTimeMilliseconds(), 0, "biurko");

    private static TaskSeries Series(RecurrenceRule rule, DateOnly? starts = null) =>
        TaskSeries.Create(
            starts ?? Today, rule, new SeriesTemplate("Śmieci"), Now, Stamp());

    private static RecurrenceRule Daily() => new(RecurrenceKind.Daily);

    private static RecurrenceRule Mondays() =>
        new(RecurrenceKind.Weekly, daysOfWeek: Weekdays.Monday);

    [Fact]
    public void Okno_jest_funkcja_serii_i_daty_a_nie_historii_urzadzenia()
    {
        // To jest cały powód przebudowy. Dwa urządzenia, które dopełniły okno
        // niezależnie i w różnych dniach, muszą dojść do tych samych wierszy —
        // inaczej scalanie przyjmie je jako różne rzeczy i seria się podwoi.
        var series = Series(Mondays());

        var biurko = SeriesWindow.Plan(series, Today);
        var telefon = SeriesWindow.Plan(series, Today.AddDays(3));

        biurko.Select(z => z.Id).Should().NotBeEmpty();

        // Dni, które mają oba urządzenia, mają u obu tę samą tożsamość. Okna nie są
        // identyczne, bo telefon patrzy trzy dni później i widzi o tyle dalej —
        // ale część wspólna musi się zgadzać wiersz w wiersz.
        var wspolne = biurko.Select(z => z.Date).Intersect(telefon.Select(z => z.Date)).ToList();

        wspolne.Should().NotBeEmpty();

        foreach (var day in wspolne)
        {
            biurko.Single(z => z.Date == day).Id
                .Should().Be(telefon.Single(z => z.Date == day).Id);
        }
    }

    [Fact]
    public void Tozsamosc_liczy_sie_z_serii_i_dnia()
    {
        var series = Series(Daily());

        foreach (var slot in SeriesWindow.Plan(series, Today))
        {
            slot.Id.Should().Be(OccurrenceId.For(series.Id, slot.Date));
            slot.Id.Version.Should().Be(8, "wyliczona z nazwy, nie losowana");
        }
    }

    [Fact]
    public void Dwie_serie_o_tym_samym_rytmie_nie_dziela_wystapien()
    {
        var first = Series(Daily());
        var second = Series(Daily());

        SeriesWindow.Plan(first, Today).Select(z => z.Id)
            .Should().NotIntersectWith(SeriesWindow.Plan(second, Today).Select(z => z.Id));
    }

    [Fact]
    public void Horyzont_to_pozniejsze_z_dwoch_liczby_i_kwartalu()
    {
        // Sześćdziesiąt wystąpień to dwa miesiące przy codziennym i czternaście
        // przy tygodniowym. Przy samym liczniku przewinięcie kalendarza o kwartał
        // pokazywałoby przy codziennej serii pustkę.
        var codziennie = SeriesWindow.Plan(Series(Daily()), Today);

        codziennie.Count.Should().BeGreaterThan(SeriesWindow.Ahead);
        codziennie[^1].Date.Should().BeOnOrAfter(Today.AddDays(SeriesWindow.Days));

        // Przy tygodniowym kwartał kończy się wcześniej niż licznik, więc rządzi licznik.
        var tygodniowo = SeriesWindow.Plan(Series(Mondays()), Today);

        tygodniowo.Count.Should().Be(SeriesWindow.Ahead);
    }

    [Fact]
    public void Okno_zaczyna_sie_od_dzisiaj_a_nie_od_jutra()
    {
        // Wystąpienie na dziś jest tym, po które się sięga najczęściej.
        SeriesWindow.Plan(Series(Daily()), Today)[0].Date.Should().Be(Today);
    }

    [Fact]
    public void Faza_rytmu_liczy_sie_od_poczatku_serii_a_nie_od_dnia_uruchomienia()
    {
        // „Co drugi dzień od poniedziałku" nie da się odtworzyć z żadnej późniejszej
        // daty: liczone od dziś wypadałoby raz na dni parzyste, raz na nieparzyste,
        // zależnie od tego, kiedy ktoś otworzył aplikację.
        var start = new DateOnly(2026, 10, 5);
        var series = Series(new RecurrenceRule(RecurrenceKind.EveryNDays, interval: 2), start);

        foreach (var today in new[] { start, start.AddDays(1), start.AddDays(10) })
        {
            foreach (var slot in SeriesWindow.Plan(series, today))
            {
                ((slot.Date.DayNumber - start.DayNumber) % 2)
                    .Should().Be(0, $"liczone w dniu {today:dd.MM} faza ma być ta sama");
            }
        }
    }

    [Fact]
    public void Seria_na_pieC_razy_daje_pieC_wystapien_i_ani_jednego_wiecej()
    {
        var series = Series(new RecurrenceRule(RecurrenceKind.Daily, count: 5));

        SeriesWindow.Plan(series, Today).Should().HaveCount(5);
    }

    [Fact]
    public void Licznik_zuzywaja_takze_wystapienia_minione()
    {
        // Seria „jeszcze pięć razy", założona pięć dni temu, ma do przodu zero —
        // a nie pięć. Minione dni są pomijane po przejściu przez nie, nie przed.
        var series = Series(
            new RecurrenceRule(RecurrenceKind.Daily, count: 5), Today.AddDays(-5));

        SeriesWindow.Plan(series, Today).Should().BeEmpty();
    }

    [Fact]
    public void Data_konca_zamyka_okno()
    {
        var last = Today.AddDays(9);
        var series = Series(new RecurrenceRule(RecurrenceKind.Daily, until: last));

        var plan = SeriesWindow.Plan(series, Today);

        plan.Should().HaveCount(10);
        plan[^1].Date.Should().Be(last);
    }

    [Fact]
    public void Koniec_serii_na_dniu_obcina_to_co_dalej()
    {
        var series = Series(Daily());

        series.EndOn(Today.AddDays(2), Stamp());

        SeriesWindow.Plan(series, Today).Select(z => z.Date)
            .Should().Equal(Today, Today.AddDays(1), Today.AddDays(2));
    }

    [Fact]
    public void Sufit_chroni_przed_rytmem_zalozonym_przez_pomylke()
    {
        // Rytm bez końca i bez licznika, z odstępem jednego dnia, przy horyzoncie
        // liczonym dniami dałby tyle wierszy, ile dni w kwartale — ale seria godzinowa
        // albo podniesiony horyzont nie mają już nic, co by je zatrzymało.
        SeriesWindow.Plan(Series(Daily()), Today).Count
            .Should().BeLessThanOrEqualTo(SeriesWindow.Ceiling);
    }

    [Fact]
    public void Rytm_od_wykonania_ma_okno_na_jedno_wystapienie()
    {
        // „Co 3 dni od wykonania" nie ma dat do wyliczenia, dopóki poprzednie nie
        // zostanie odhaczone. Sześćdziesiąt wierszy na datach zgadniętych z założenia
        // „wszystko na czas" trzeba by przy każdym spóźnieniu skasować i postawić od
        // nowa — a do tej chwili kalendarz pokazywałby rozkład, którego nikt nie obiecał.
        var series = Series(new RecurrenceRule(
            RecurrenceKind.EveryNDays, interval: 3, anchor: RecurrenceAnchor.FromCompletion));

        SeriesWindow.Plan(series, Today).Should().HaveCount(1);
    }

    [Fact]
    public void Rytm_codzienny_ma_pelne_okno()
    {
        // Przy odstępie jednego dnia zaczepienie na wykonaniu i na kalendarzu znaczą
        // to samo, więc „codziennie" domyślnie stoi na kalendarzu — i ma okno.
        SeriesWindow.Plan(Series(new RecurrenceRule(RecurrenceKind.Daily)), Today).Count
            .Should().BeGreaterThan(SeriesWindow.Ahead);
    }

    [Fact]
    public void Szablon_i_regula_wracaja_z_zapisu()
    {
        // Jedna kolumna tekstu na jedną decyzję — ta sama zasada, co przy regule.
        var template = new SeriesTemplate(
            "Śmieci",
            Note: "do altanki",
            DoTime: new TimeOnly(19, 0),
            EstimatedMinutes: 10,
            Leads: [15, 60],
            DeadlineOffsetDays: 2);

        var back = SeriesTemplate.FromJson(template.ToJson());

        back.Should().Be(template);
    }

    [Fact]
    public void Zapis_szablonu_z_nowszej_wersji_nie_wywraca_odczytu()
    {
        // Spec 9.4: scalanie musi przetrwać zapis, którego ta wersja nie rozumie.
        SeriesTemplate.FromJson("{ to nie jest json }").Should().BeNull();
        SeriesTemplate.FromJson(null).Should().BeNull();
    }
}
