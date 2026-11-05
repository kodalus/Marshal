using System.Collections.Concurrent;

namespace Marshal.Domain.Lunar;

/// <summary>
/// Kalendarz księżycowy: nakładka na kalendarz gregoriański, nigdy zamiennik.
/// </summary>
/// <remarks>
/// <para>
/// Dwie reguły i nic więcej. <b>Miesiąc</b> trwa od dnia astronomicznego nowiu do dnia
/// przed następnym — stąd 29 albo 30 dni, bez trzeciej możliwości. <b>Rok</b> zaczyna się
/// pierwszym nowiem po przesileniu zimowym i kończy dzień przed pierwszym nowiem
/// po przesileniu następnym — stąd 12 albo 13 miesięcy, a trzynasty jest zawsze ostatni.
/// </para>
/// <para>
/// <b>Dniem nowiu jest data kalendarzowa tej chwili w strefie użytkownika</b>, choćby
/// nów wypadł o 23:50. To nie jest zaokrąglenie do najbliższego dnia i nie ma być:
/// pytanie „który dziś dzień księżycowy” ma jedną odpowiedź dla całej doby, a doba
/// zaczyna się o północy tam, gdzie ktoś stoi. Dlatego strefa jest wejściem rachunku,
/// nie jego ozdobą — ta sama chwila nowiu daje w Warszawie i w Los Angeles różne daty.
/// </para>
/// <para>
/// <b>Numeracja roku.</b> Początek roku księżycowego wypada zawsze między 20 grudnia
/// a 21 stycznia — w latach 1900–2100 najwcześniej 21 grudnia, najpóźniej 21 stycznia.
/// Rok dostaje numer roku gregoriańskiego swojego pierwszego dnia,
/// powiększony o jeden, gdy ten dzień wypada w grudniu — czyli rok zaczynający się
/// 27 grudnia 2027 jest rokiem 2028, tak samo jak rok 2026 zaczynający się 18 stycznia
/// 2026. Kolejne początki dzieli 354 albo 384 dni, więc numery wychodzą kolejne, bez
/// dziur i bez dwóch lat o tym samym numerze.
/// </para>
/// <para>
/// Reguła, którą zastąpiła: „numer roku następnego, gdy początek wypada 22–31 grudnia”.
/// Miała dziurę. Przesilenie wypada 20, 21 albo 22 grudnia, więc pierwszy nów po nim może
/// wypaść 20 albo 21 grudnia — i taki rok nie dostawałby numeru z żadnej z dwóch gałęzi
/// poprawnie: jako grudniowy zostałby przy numerze roku starego, a rok następny, zaczynający
/// się w styczniu trzynaście miesięcy później, dostałby ten sam numer. Warunek „grudzień”
/// bez dnia nie ma tej dziury, bo początek roku nie wypada w grudniu nigdy wcześniej niż
/// po przesileniu.
/// </para>
/// <para>
/// <b>Zakres.</b> Rachunek liczy dla każdej daty, ale sprawdzony i obiecany jest
/// przedział 1900–2100. Dalej rośnie niepewność ΔT — obrotu Ziemi nikt na sto lat
/// naprzód nie zna — a nie samego rachunku faz.
/// </para>
/// <para>
/// Modułu nie wiąże nic z interfejsem: wejściem jest data i strefa, wyjściem liczby.
/// Pulpit i telefon dzielą ten jeden rachunek, bo oba mają ten sam kalendarz pokazywać.
/// </para>
/// </remarks>
public static class LunarCalendar
{
    /// <summary>
    /// Policzone granice miesięcy, po roku księżycowym i strefie.
    /// </summary>
    /// <remarks>
    /// Pamięć podręczna, bo siatka miesiąca pyta o to samo trzydzieści razy pod rząd,
    /// a jeden rok to kilkanaście rachunków fazy i dwa rachunki przesilenia. Rok wchodzi
    /// do pamięci wtedy, gdy ktoś o niego zapyta — nie ma tu żadnego wypełniania zakresu
    /// z góry, więc obiecane dwa stulecia nic nie kosztują, dopóki nikt po nich nie chodzi.
    /// Klucz zawiera identyfikator strefy, bo granice dni są od strefy zależne.
    /// </remarks>
    private static readonly ConcurrentDictionary<(int Year, string Zone), IReadOnlyList<LunarMonth>> Cached = new();

    /// <summary>Data księżycowa dnia <paramref name="day"/> widziana ze strefy <paramref name="zone"/>.</summary>
    public static LunarDate Of(DateOnly day, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        // Rok księżycowy prawie pokrywa się z gregoriańskim, ale nie całkiem: początek
        // stycznia może jeszcze należeć do roku poprzedniego, a koniec grudnia już
        // do następnego. Jeden krok poprawki zawsze wystarcza, i to jest rachunek,
        // a nie nadzieja: rok o numerze N zaczyna się najpóźniej 21 stycznia N i kończy
        // najwcześniej 20 grudnia N, więc data z roku N leżąca poza jego granicami wypada
        // albo w styczniu — czyli w roku N−1 — albo w grudniu, czyli w roku N+1.
        var year = day.Year;
        var months = Months(year, zone);

        if (day < months[0].Starts)
        {
            months = Months(--year, zone);
        }
        else if (day > months[^1].Ends)
        {
            months = Months(++year, zone);
        }

        var month = months.First(z => day >= z.Starts && day <= z.Ends);

        return new LunarDate(
            year,
            month.Number,
            day.DayNumber - month.Starts.DayNumber + 1,
            month.Length,
            day == month.Starts,
            day == month.FullMoon,
            month.IsThirteenth);
    }

    /// <summary>Miesiące roku księżycowego <paramref name="year"/> — dwanaście albo trzynaście.</summary>
    public static IReadOnlyList<LunarMonth> Months(int year, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        return Cached.GetOrAdd((year, zone.Id), key => Build(key.Year, zone));
    }

    private static IReadOnlyList<LunarMonth> Build(int year, TimeZoneInfo zone)
    {
        // Rok księżycowy o numerze N zaczyna się pierwszym nowiem po przesileniu roku
        // gregoriańskiego N−1 — i tylko dlatego numeracja wychodzi kolejna.
        var opens = FirstNewMoonAfterSolstice(year - 1);
        var closes = FirstNewMoonAfterSolstice(year);

        var months = new List<LunarMonth>();

        for (var k = opens; k < closes; k++)
        {
            var starts = Local(Meeus.NewMoonUtc(k), zone);
            var ends = Local(Meeus.NewMoonUtc(k + 1), zone).AddDays(-1);
            var full = Local(Meeus.FullMoonUtc(k), zone);

            months.Add(new LunarMonth(
                year,
                (int)(k - opens) + 1,
                starts,
                ends,
                full >= starts && full <= ends ? full : null));
        }

        return months;
    }

    /// <summary>Numer Meeusa pierwszego nowiu po przesileniu zimowym roku <paramref name="year"/>.</summary>
    /// <remarks>
    /// Porównanie idzie po <b>chwilach</b>, nie po datach: nów tego samego dnia co
    /// przesilenie, ale o godzinę wcześniej, należy jeszcze do roku starego. Dlatego oba
    /// rachunki zostają w czasie uniwersalnym, a na daty przechodzi się dopiero potem.
    /// Zgadywanie początkowe jest przybliżone celowo — obie pętle i tak dochodzą
    /// do właściwego numeru, a dzięki temu nie ma tu założenia, które mogłoby się zepsuć
    /// na krańcu zakresu.
    /// </remarks>
    private static long FirstNewMoonAfterSolstice(int year)
    {
        var solstice = Meeus.WinterSolsticeUtc(year);

        // Lunacji w roku jest 12,3685; grudzień to 0,96 roku.
        var k = (long)Math.Round((year + 0.96 - 2000) * 12.3685);

        while (Meeus.NewMoonUtc(k) > solstice)
        {
            k--;
        }

        while (Meeus.NewMoonUtc(k) <= solstice)
        {
            k++;
        }

        return k;
    }

    private static DateOnly Local(DateTime utc, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, zone));
}
