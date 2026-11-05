using FluentAssertions;
using Marshal.Domain.Lunar;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Kalendarz księżycowy: czy chwile zjawisk są prawdziwe i czy dni z nich wychodzące
/// biegną bez dziur.
/// </summary>
/// <remarks>
/// <para>
/// Dwa rodzaje sprawdzeń, bo są tu dwa rodzaje błędów. Pierwszy: <b>zły rachunek</b> —
/// przepisany nie tak współczynnik, pomylony stopień z radianem, zapomniane ΔT.
/// Takiego błędu nie widać po kształcie wyniku: miesiące nadal mają 29 albo 30 dni,
/// tylko wypadają w złych dniach. Dlatego chwile nowiu, pełni i przesilenia porównują się
/// z <b>tablicami zewnętrznymi</b> — z czymś, co nie pochodzi z tego kodu.
/// </para>
/// <para>
/// Drugi: <b>zły podział</b> — dzień zgubiony albo policzony dwa razy przy granicy
/// miesiąca, przy zmianie czasu, przy przejściu roku. Takiego błędu nie widać po
/// chwilach, bo chwile są dobre. Dlatego drugi zestaw przechodzi przez <b>każdy dzień
/// od 1900 do 2100</b> i pilnuje, żeby numeracja rosła o jeden i ani razu się nie
/// potknęła. Siedemdziesiąt trzy tysiące dni to tyle, ile zajmuje kilkanaście lat
/// używania aplikacji — a tu idzie w jedną stronę, bez przerwy.
/// </para>
/// <para>
/// Wartości odniesienia pochodzą z biblioteki <c>ephem</c> (silnik XEphem), czyli
/// z <b>innego rachunku niż ten</b>: nie z rozdziału 49 Meeusa, tylko z pełnych szeregów
/// ELP. Zgodność dwóch niezależnych rachunków znaczy więcej niż zgodność kodu z samym
/// sobą. Porównanie całych dwóch stuleci — 2487 nowiów, 2486 pełni i 202 przesilenia —
/// dało różnicę najwyżej 34, 35 i 56 sekund; tutaj zostaje garść lat, żeby test był
/// czytelny, a <b>granica dwóch minut</b> jest po to, żeby złapać błąd w rachunku,
/// a nie żeby udawać dokładność, której te wzory nie mają.
/// </para>
/// </remarks>
public sealed class LunarCalendarTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    /// <summary>
    /// Strefa o godzinę dalej na wschód — do pokazania, że data nowiu zależy od strefy.
    /// </summary>
    private static readonly TimeZoneInfo Cairo = TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo");

    /// <summary>Ile wolno się różnić od tablic. Zob. uwagi do klasy.</summary>
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(2);

    /// <summary>Nowie 2025–2028 w czasie uniwersalnym, z tablic zewnętrznych.</summary>
    private static readonly DateTime[] NewMoons =
    [
        new(2025, 1, 29, 12, 35, 54, DateTimeKind.Utc),
        new(2025, 2, 28, 0, 44, 45, DateTimeKind.Utc),
        new(2025, 3, 29, 10, 57, 46, DateTimeKind.Utc),
        new(2025, 4, 27, 19, 31, 5, DateTimeKind.Utc),
        new(2025, 5, 27, 3, 2, 17, DateTimeKind.Utc),
        new(2025, 6, 25, 10, 31, 32, DateTimeKind.Utc),
        new(2025, 7, 24, 19, 11, 6, DateTimeKind.Utc),
        new(2025, 8, 23, 6, 6, 27, DateTimeKind.Utc),
        new(2025, 9, 21, 19, 54, 3, DateTimeKind.Utc),
        new(2025, 10, 21, 12, 25, 7, DateTimeKind.Utc),
        new(2025, 11, 20, 6, 47, 13, DateTimeKind.Utc),
        new(2025, 12, 20, 1, 43, 17, DateTimeKind.Utc),
        new(2026, 1, 18, 19, 51, 54, DateTimeKind.Utc),
        new(2026, 2, 17, 12, 1, 4, DateTimeKind.Utc),
        new(2026, 3, 19, 1, 23, 24, DateTimeKind.Utc),
        new(2026, 4, 17, 11, 51, 44, DateTimeKind.Utc),
        new(2026, 5, 16, 20, 0, 58, DateTimeKind.Utc),
        new(2026, 6, 15, 2, 54, 4, DateTimeKind.Utc),
        new(2026, 7, 14, 9, 43, 31, DateTimeKind.Utc),
        new(2026, 8, 12, 17, 36, 39, DateTimeKind.Utc),
        new(2026, 9, 11, 3, 26, 55, DateTimeKind.Utc),
        new(2026, 10, 10, 15, 50, 1, DateTimeKind.Utc),
        new(2026, 11, 9, 7, 2, 3, DateTimeKind.Utc),
        new(2026, 12, 9, 0, 51, 46, DateTimeKind.Utc),
        new(2027, 1, 7, 20, 24, 18, DateTimeKind.Utc),
        new(2027, 2, 6, 15, 56, 2, DateTimeKind.Utc),
        new(2027, 3, 8, 9, 29, 24, DateTimeKind.Utc),
        new(2027, 4, 6, 23, 51, 5, DateTimeKind.Utc),
        new(2027, 5, 6, 10, 58, 32, DateTimeKind.Utc),
        new(2027, 6, 4, 19, 40, 15, DateTimeKind.Utc),
        new(2027, 7, 4, 3, 1, 58, DateTimeKind.Utc),
        new(2027, 8, 2, 10, 5, 8, DateTimeKind.Utc),
        new(2027, 8, 31, 17, 41, 5, DateTimeKind.Utc),
        new(2027, 9, 30, 2, 35, 59, DateTimeKind.Utc),
        new(2027, 10, 29, 13, 36, 28, DateTimeKind.Utc),
        new(2027, 11, 28, 3, 24, 21, DateTimeKind.Utc),
        new(2027, 12, 27, 20, 12, 14, DateTimeKind.Utc),
        new(2028, 1, 26, 15, 12, 25, DateTimeKind.Utc),
        new(2028, 2, 25, 10, 37, 20, DateTimeKind.Utc),
        new(2028, 3, 26, 4, 31, 14, DateTimeKind.Utc),
        new(2028, 4, 24, 19, 46, 50, DateTimeKind.Utc),
        new(2028, 5, 24, 8, 16, 12, DateTimeKind.Utc),
        new(2028, 6, 22, 18, 27, 26, DateTimeKind.Utc),
        new(2028, 7, 22, 3, 1, 35, DateTimeKind.Utc),
        new(2028, 8, 20, 10, 43, 44, DateTimeKind.Utc),
        new(2028, 9, 18, 18, 23, 39, DateTimeKind.Utc),
        new(2028, 10, 18, 2, 56, 42, DateTimeKind.Utc),
        new(2028, 11, 16, 13, 17, 55, DateTimeKind.Utc),
        new(2028, 12, 16, 2, 6, 13, DateTimeKind.Utc),
    ];

    /// <summary>Pełnie 2026 w czasie uniwersalnym, z tablic zewnętrznych.</summary>
    private static readonly DateTime[] FullMoons =
    [
        new(2026, 1, 3, 10, 2, 50, DateTimeKind.Utc),
        new(2026, 2, 1, 22, 9, 10, DateTimeKind.Utc),
        new(2026, 3, 3, 11, 37, 49, DateTimeKind.Utc),
        new(2026, 4, 2, 2, 11, 54, DateTimeKind.Utc),
        new(2026, 5, 1, 17, 23, 6, DateTimeKind.Utc),
        new(2026, 5, 31, 8, 45, 7, DateTimeKind.Utc),
        new(2026, 6, 29, 23, 56, 35, DateTimeKind.Utc),
        new(2026, 7, 29, 14, 35, 37, DateTimeKind.Utc),
        new(2026, 8, 28, 4, 18, 26, DateTimeKind.Utc),
        new(2026, 9, 26, 16, 48, 57, DateTimeKind.Utc),
        new(2026, 10, 26, 4, 11, 44, DateTimeKind.Utc),
        new(2026, 11, 24, 14, 53, 29, DateTimeKind.Utc),
        new(2026, 12, 24, 1, 28, 9, DateTimeKind.Utc),
    ];

    /// <summary>Przesilenia zimowe 2024–2028 w czasie uniwersalnym, z tablic zewnętrznych.</summary>
    private static readonly (int Year, DateTime Moment)[] Solstices =
    [
        (2024, new DateTime(2024, 12, 21, 9, 20, 20, DateTimeKind.Utc)),
        (2025, new DateTime(2025, 12, 21, 15, 2, 51, DateTimeKind.Utc)),
        (2026, new DateTime(2026, 12, 21, 20, 49, 59, DateTimeKind.Utc)),
        (2027, new DateTime(2027, 12, 22, 2, 41, 53, DateTimeKind.Utc)),
        (2028, new DateTime(2028, 12, 21, 8, 19, 23, DateTimeKind.Utc)),
    ];

    [Fact]
    public void Chwile_nowiu_zgadzaja_sie_z_tablicami()
    {
        foreach (var expected in NewMoons)
        {
            var computed = Meeus.NewMoonUtc(NewMoonIndex(expected));

            (computed - expected).Duration().Should().BeLessThan(
                Tolerance,
                $"nów {expected:yyyy-MM-dd HH:mm} wyszedł jako {computed:yyyy-MM-dd HH:mm}");
        }
    }

    [Fact]
    public void Chwile_pelni_zgadzaja_sie_z_tablicami()
    {
        foreach (var expected in FullMoons)
        {
            // Pełnia należy do tego miesiąca księżycowego, który zaczął nów przed nią —
            // więc numer bierze się z nowiu o jakieś dwa tygodnie wcześniejszego.
            var computed = Meeus.FullMoonUtc(NewMoonIndex(expected.AddDays(-14)));

            (computed - expected).Duration().Should().BeLessThan(
                Tolerance,
                $"pełnia {expected:yyyy-MM-dd HH:mm} wyszła jako {computed:yyyy-MM-dd HH:mm}");
        }
    }

    [Fact]
    public void Chwile_przesilenia_zimowego_zgadzaja_sie_z_tablicami()
    {
        foreach (var (year, expected) in Solstices)
        {
            var computed = Meeus.WinterSolsticeUtc(year);

            (computed - expected).Duration().Should().BeLessThan(
                Tolerance,
                $"przesilenie {year} wyszło jako {computed:yyyy-MM-dd HH:mm}");
        }
    }

    [Fact]
    public void Rok_zaczyna_sie_pierwszym_nowiem_po_przesileniu()
    {
        // Sprawdzane na dwóch latach o różnym kształcie: 2026 ma dwanaście miesięcy,
        // 2025 trzynaście. W obu początek to dzień pierwszego nowiu po przesileniu
        // roku poprzedniego — i to jest cała reguła roku.
        LunarCalendar.Months(2025, Warsaw)[0].Starts.Should().Be(new DateOnly(2024, 12, 30));
        LunarCalendar.Months(2026, Warsaw)[0].Starts.Should().Be(new DateOnly(2026, 1, 18));
        LunarCalendar.Months(2027, Warsaw)[0].Starts.Should().Be(new DateOnly(2027, 1, 7));
        LunarCalendar.Months(2028, Warsaw)[0].Starts.Should().Be(new DateOnly(2027, 12, 27));

        // Rok 2025 zaczyna się 30 grudnia 2024, bo nów z 20 grudnia 2024 wypadł
        // jeszcze przed przesileniem (21 grudnia) — i dlatego do roku 2025 nie należy.
        LunarCalendar.Months(2025, Warsaw)[^1].Ends.Should().Be(new DateOnly(2026, 1, 17));
        LunarCalendar.Months(2026, Warsaw)[^1].Ends.Should().Be(new DateOnly(2027, 1, 6));
    }

    [Fact]
    public void Numer_roku_to_rok_poczatku_a_w_grudniu_o_jeden_wiekszy()
    {
        // Reguła numeracji na całym obiecanym zakresie. Początek roku księżycowego wypada
        // zawsze między 20 grudnia a około 20 stycznia, więc wystarczy jeden wyjątek:
        // grudzień należy już do roku następnego.
        for (var year = 1900; year <= 2100; year++)
        {
            var opens = LunarCalendar.Months(year, Warsaw)[0].Starts;
            var expected = opens.Month == 12 ? opens.Year + 1 : opens.Year;

            expected.Should().Be(year, $"rok księżycowy {year} zaczyna się {opens:yyyy-MM-dd}");
            opens.Month.Should().BeOneOf(12, 1);
        }
    }

    [Fact]
    public void Lata_stykaja_sie_bez_dziur_i_bez_zakladek()
    {
        // Numeracja mogłaby być spójna co do reguły i wciąż gubić dni: wystarczy, żeby
        // rok kończył się dzień za wcześnie. Granica dwóch lat musi być jedna.
        var previous = LunarCalendar.Months(1900, Warsaw);

        for (var year = 1901; year <= 2100; year++)
        {
            var current = LunarCalendar.Months(year, Warsaw);

            current[0].Starts.Should().Be(
                previous[^1].Ends.AddDays(1),
                $"rok {year} ma zaczynać się nazajutrz po końcu roku {year - 1}");

            previous = current;
        }
    }

    [Fact]
    public void Rok_ma_dwanascie_albo_trzynascie_miesiecy_a_trzynasty_jest_ostatni()
    {
        var thirteens = 0;

        for (var year = 1900; year <= 2100; year++)
        {
            var months = LunarCalendar.Months(year, Warsaw);

            months.Count.Should().BeInRange(12, 13, $"rok {year}");
            months.Select(z => z.Number).Should().Equal(Enumerable.Range(1, months.Count));

            // Trzynasty, jeśli jest, jest ostatni — a ostatni jest trzynasty tylko wtedy,
            // gdy miesięcy jest trzynaście.
            months.Count(z => z.IsThirteenth).Should().Be(months.Count == 13 ? 1 : 0);
            months[^1].IsThirteenth.Should().Be(months.Count == 13, $"rok {year}");

            if (months.Count == 13)
            {
                thirteens++;
            }
        }

        // Dwanaście miesięcy księżycowych to 354 dni, rok słoneczny 365 — nadwyżka
        // zbiera się do pełnego miesiąca mniej więcej raz na dwa albo trzy lata.
        // Na dwieście jeden lat wypada to siedemdziesiąt kilka razy; gdyby wypadło
        // zero albo połowę, reguła roku byłaby zepsuta, a kształt wyniku i tak poprawny.
        thirteens.Should().BeInRange(70, 78);
    }

    [Fact]
    public void Kazdy_miesiac_ma_dwadziescia_dziewiec_albo_trzydziesci_dni()
    {
        // Trzeciej możliwości nie ma: miesiąc trwa od nowiu do dnia przed następnym,
        // a nowie dzieli 29,27–29,83 dnia.
        for (var year = 1900; year <= 2100; year++)
        {
            foreach (var month in LunarCalendar.Months(year, Warsaw))
            {
                month.Length.Should().BeInRange(29, 30, $"{year}/{month.Number}");
                month.Ends.Should().BeOnOrAfter(month.Starts);
            }
        }
    }

    [Fact]
    public void Granice_miesiecy_w_roku_stykaja_sie_bez_dziur()
    {
        for (var year = 1900; year <= 2100; year++)
        {
            var months = LunarCalendar.Months(year, Warsaw);

            for (var index = 1; index < months.Count; index++)
            {
                months[index].Starts.Should().Be(
                    months[index - 1].Ends.AddDays(1),
                    $"{year}/{months[index].Number}");
            }
        }
    }

    [Fact]
    public void Kazdy_miesiac_ma_dokladnie_jeden_dzien_pelni()
    {
        // Pełnia wypada czternastego albo piętnastego dnia: zawsze wewnątrz miesiąca,
        // nigdy na granicy obok. Gdyby rachunek pełni rozjechał się z rachunkiem nowiu,
        // pełnia wypadłaby poza miesiącem i tutaj byłoby to widać.
        for (var year = 1900; year <= 2100; year++)
        {
            foreach (var month in LunarCalendar.Months(year, Warsaw))
            {
                month.FullMoon.Should().NotBeNull($"{year}/{month.Number}");

                var day = month.FullMoon!.Value.DayNumber - month.Starts.DayNumber + 1;

                day.Should().BeInRange(13, 17, $"{year}/{month.Number}");
            }
        }
    }

    [Fact]
    public void Dni_biegna_bez_dziur_i_bez_powtorzen_przez_dwa_stulecia()
    {
        // Najważniejszy test w tym pliku. Przechodzi dzień po dniu przez dwieście lat
        // i pilnuje jednej rzeczy: numer dnia rośnie o jeden, a jeśli wraca do jedynki,
        // to poprzedni dzień był ostatnim w swoim miesiącu. Każda dziura i każde
        // powtórzenie łamie to natychmiast, niezależnie od tego, czy wzięło się
        // z zaokrąglenia chwili, ze zmiany czasu, czy z przejścia roku.
        var day = new DateOnly(1900, 2, 1);
        var last = new DateOnly(2100, 11, 30);
        var previous = LunarCalendar.Of(day, Warsaw);
        var counted = 1;

        while (day < last)
        {
            day = day.AddDays(1);
            var current = LunarCalendar.Of(day, Warsaw);
            counted++;

            if (current.Day == previous.Day + 1)
            {
                current.Month.Should().Be(previous.Month, $"{day:yyyy-MM-dd}");
                current.Year.Should().Be(previous.Year, $"{day:yyyy-MM-dd}");
            }
            else
            {
                current.Day.Should().Be(1, $"{day:yyyy-MM-dd} po dniu {previous.Day}");
                previous.Day.Should().Be(previous.Length, $"{day:yyyy-MM-dd} kończy miesiąc skrótem");

                var sameYear = current.Year == previous.Year && current.Month == previous.Month + 1;
                var nextYear = current.Year == previous.Year + 1 && current.Month == 1;

                (sameYear || nextYear).Should().BeTrue(
                    $"{day:yyyy-MM-dd}: skok z {previous.Year}/{previous.Month} "
                    + $"na {current.Year}/{current.Month}");
            }

            current.IsNewMoon.Should().Be(current.Day == 1, $"{day:yyyy-MM-dd}");

            previous = current;
        }

        counted.Should().BeGreaterThan(73_000, "dwieście lat to tyle dni");
    }

    [Fact]
    public void Dzien_nowiu_to_data_lokalna_tej_chwili_a_nie_najblizsza()
    {
        // Nów 9 lutego 2024 o 22:59 czasu uniwersalnego. W Warszawie jest wtedy 23:59
        // tego samego dnia, w Kairze 00:59 następnego. Ta sama chwila, dwie różne daty —
        // i tak ma być, bo dzień księżycowy zaczyna się o lokalnej północy.
        LunarCalendar.Of(new DateOnly(2024, 2, 9), Warsaw).IsNewMoon.Should().BeTrue();
        LunarCalendar.Of(new DateOnly(2024, 2, 9), Warsaw).Day.Should().Be(1);

        LunarCalendar.Of(new DateOnly(2024, 2, 9), Cairo).IsNewMoon.Should().BeFalse();
        LunarCalendar.Of(new DateOnly(2024, 2, 10), Cairo).IsNewMoon.Should().BeTrue();

        // Dzień przed nowiem jest ostatnim dniem poprzedniego miesiąca, nie zerowym
        // dniem nowego: w Kairze 9 lutego domyka miesiąc trzydziestodniowy.
        var closing = LunarCalendar.Of(new DateOnly(2024, 2, 9), Cairo);
        closing.Day.Should().Be(closing.Length);
    }

    [Fact]
    public void Now_minute_przed_polnoca_nalezy_do_dnia_konczacego_sie()
    {
        // Najcieńszy przypadek w całym obiecanym zakresie: 3 maja 1924 nów wypada
        // w Warszawie o 23:59:41 — dziewiętnaście sekund przed północą. Dwa niezależne
        // rachunki różnią się tu o sześć sekund, więc data jeszcze się zgadza; gdyby
        // różniły się o pół minuty, zgadzać by się przestała. Dlatego ten dzień jest
        // tutaj wpisany wprost: on jest miarą tego, ile w tym rachunku zostaje zapasu.
        LunarCalendar.Of(new DateOnly(1924, 5, 3), Warsaw).IsNewMoon.Should().BeTrue();
        LunarCalendar.Of(new DateOnly(1924, 5, 4), Warsaw).Day.Should().Be(2);
    }

    [Fact]
    public void Now_w_dniu_przesilenia_liczy_sie_po_chwili_a_nie_po_dacie()
    {
        // Para przypadków, która rozstrzyga, co znaczy „pierwszy nów po przesileniu”.
        //
        // 2052: przesilenie 21 grudnia o 04:17:12, nów tego samego dnia o 04:14:48 —
        // dwie i pół minuty WCZEŚNIEJ. Nów należy więc jeszcze do roku starego, a rok
        // 2053 zaczyna się dopiero nowiem styczniowym, miesiąc później.
        LunarCalendar.Months(2053, Warsaw)[0].Starts.Should().Be(new DateOnly(2053, 1, 20));
        LunarCalendar.Months(2052, Warsaw)[^1].Ends.Should().Be(new DateOnly(2053, 1, 19));

        // 2033: przesilenie 21 grudnia o 13:45, nów tego samego dnia o 18:46 — pięć godzin
        // PÓŹNIEJ. Ten sam dzień kalendarzowy, odwrotny wynik: nów otwiera nowy rok.
        LunarCalendar.Months(2034, Warsaw)[0].Starts.Should().Be(new DateOnly(2033, 12, 21));

        // I numer tego roku to 2034, nie 2033 — bo początek wypadł w grudniu.
        LunarCalendar.Of(new DateOnly(2033, 12, 21), Warsaw).Year.Should().Be(2034);
        LunarCalendar.Of(new DateOnly(2033, 12, 21), Warsaw).Day.Should().Be(1);
    }

    [Fact]
    public void Zmiana_czasu_nie_gubi_dnia_ani_go_nie_powtarza()
    {
        // Ostatnia niedziela marca i ostatnia niedziela października 2026. Doba ma wtedy
        // 23 albo 25 godzin, a dzień księżycowy ma nadal rosnąć o jeden — bo liczy się
        // po dacie lokalnej, nie po liczbie godzin, które minęły.
        foreach (var start in new DateOnly[] { new(2026, 3, 27), new(2026, 10, 23) })
        {
            for (var offset = 1; offset <= 6; offset++)
            {
                var before = LunarCalendar.Of(start.AddDays(offset - 1), Warsaw);
                var after = LunarCalendar.Of(start.AddDays(offset), Warsaw);

                var next = after.Day == before.Day + 1 || (after.Day == 1 && before.Day == before.Length);

                next.Should().BeTrue(
                    $"{start.AddDays(offset):yyyy-MM-dd}: z dnia {before.Day} na {after.Day}");
            }
        }
    }

    [Fact]
    public void Rok_przestepny_i_rok_stuletni_nie_psuja_numeracji()
    {
        // 2024 ma 29 lutego, 2100 nie ma — choć dzieli się przez cztery. Oba przejścia
        // idą przez ten sam rachunek dni, więc oba warto przejść dzień po dniu.
        foreach (var year in new[] { 2024, 2100 })
        {
            var day = new DateOnly(year, 2, 20);
            var previous = LunarCalendar.Of(day, Warsaw);

            while (day < new DateOnly(year, 3, 10))
            {
                day = day.AddDays(1);
                var current = LunarCalendar.Of(day, Warsaw);

                var next = current.Day == previous.Day + 1
                    || (current.Day == 1 && previous.Day == previous.Length);

                next.Should().BeTrue($"{day:yyyy-MM-dd}: z {previous.Day} na {current.Day}");

                previous = current;
            }
        }

        new DateTime(2100, 1, 1).AddDays(59).Day.Should().Be(1, "rok 2100 nie jest przestępny");
    }

    [Fact]
    public void Ta_sama_data_w_innej_strefie_daje_spojny_kalendarz()
    {
        // Strefa jest wejściem rachunku, więc każdą trzeba liczyć osobno — ale w każdej
        // ma wyjść kalendarz, nie mozaika. Los Angeles jest o osiem albo dziewięć godzin
        // za Warszawą, więc granice miesięcy wypadają tam czasem o dzień wcześniej.
        var angeles = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");

        for (var year = 2000; year <= 2050; year++)
        {
            var months = LunarCalendar.Months(year, angeles);

            months.Count.Should().BeInRange(12, 13, $"rok {year} w Los Angeles");

            for (var index = 1; index < months.Count; index++)
            {
                months[index].Starts.Should().Be(months[index - 1].Ends.AddDays(1));
                months[index].Length.Should().BeInRange(29, 30);
            }
        }
    }

    [Fact]
    public void Dzis_wypada_tam_gdzie_wypada()
    {
        // Jeden zapis wprost, żeby było o co oprzeć wzrok przy pierwszym uruchomieniu:
        // 5 listopada 2026 to dwudziesty siódmy dzień dziesiątego miesiąca roku 2026,
        // miesiąca trzydziestodniowego, który zaczął się 10 października.
        var today = LunarCalendar.Of(new DateOnly(2026, 11, 5), Warsaw);

        today.Year.Should().Be(2026);
        today.Month.Should().Be(10);
        today.Day.Should().Be(27);
        today.Length.Should().Be(30);
        today.IsNewMoon.Should().BeFalse();
        today.IsFullMoon.Should().BeFalse();
        today.IsThirteenth.Should().BeFalse();
    }

    /// <summary>
    /// Numer Meeusa nowiu wypadającego w okolicy <paramref name="about"/>.
    /// </summary>
    /// <remarks>
    /// Test nie zna numeracji i nie ma jej znać: zgaduje z odstępu średniego, a potem
    /// dochodzi pętlą. Dzięki temu sprawdza chwile, a nie to, czy ktoś dobrze policzył
    /// indeks w tablicy.
    /// </remarks>
    private static long NewMoonIndex(DateTime about)
    {
        var k = (long)Math.Round((about - new DateTime(2000, 1, 6, 18, 14, 0, DateTimeKind.Utc))
            .TotalDays / 29.530588861);

        while (Meeus.NewMoonUtc(k) - about > TimeSpan.FromDays(15))
        {
            k--;
        }

        while (about - Meeus.NewMoonUtc(k) > TimeSpan.FromDays(15))
        {
            k++;
        }

        return k;
    }
}
